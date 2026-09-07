using System.Security.Claims;
using System.Text;
using GameServer.Contracts;
using GameServer.Gateway.Realtime;
using GameServer.Infrastructure;
using GameServer.Infrastructure.Content;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.IdentityModel.Tokens;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Orleans.Hosting;
using Orleans;
using Orleans.Configuration;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
var jwt = builder.Configuration.GetSection("Jwt");
var signingKey = jwt["SigningKey"] ?? throw new InvalidOperationException("Jwt:SigningKey is required.");

builder.Services.AddGameInfrastructure(builder.Configuration);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks();
builder.Services.AddAuthorization(options => options.AddPolicy("ContentAdmin", policy => policy.RequireRole("content-admin")));
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
{
    options.TokenValidationParameters = new()
    {
        ValidateIssuer = true, ValidIssuer = jwt["Issuer"], ValidateAudience = true, ValidAudience = jwt["Audience"],
        ValidateIssuerSigningKey = true, IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
        ValidateLifetime = true, ClockSkew = TimeSpan.FromMinutes(1)
    };
});
builder.Services.AddRateLimiter(options => options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
    RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 })));
builder.Services.AddOpenTelemetry().WithTracing(tracing => tracing.AddAspNetCoreInstrumentation()).WithMetrics(metrics => metrics.AddAspNetCoreInstrumentation().AddRuntimeInstrumentation());

builder.Host.UseOrleans(silo =>
{
    silo.Configure<ClusterOptions>(options => { options.ClusterId = "orleans-game"; options.ServiceId = "orleans-game"; });
    if (builder.Configuration.GetValue<bool>("Orleans:UseAdoNet"))
    {
        var postgres = builder.Configuration.GetConnectionString("Postgres")!;
        silo.UseAdoNetClustering(options => { options.Invariant = "Npgsql"; options.ConnectionString = postgres; });
        silo.AddAdoNetGrainStorage("gameStore", options => { options.Invariant = "Npgsql"; options.ConnectionString = postgres; });
    }
    else
    {
        silo.UseLocalhostClustering();
        silo.AddMemoryGrainStorage("gameStore");
    }
});

var app = builder.Build();
app.UseExceptionHandler();
app.UseHttpsRedirection();
app.UseRateLimiter();
app.UseWebSockets();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapHealthChecks("/health/ready");

var auth = app.MapGroup("/api/auth");
auth.MapPost("/register", async (RegisterRequest request, UserManager<IdentityUser> users) =>
{
    var user = new IdentityUser { UserName = request.Email, Email = request.Email };
    var result = await users.CreateAsync(user, request.Password);
    return result.Succeeded ? Results.Created($"/api/accounts/{user.Id}", new { user.Id, user.Email }) : Results.ValidationProblem(result.Errors.GroupBy(x => x.Code).ToDictionary(x => x.Key, x => x.Select(x => x.Description).ToArray()));
});
auth.MapPost("/login", async (LoginRequest request, UserManager<IdentityUser> users) =>
{
    var user = await users.FindByEmailAsync(request.Email);
    if (user is null || !await users.CheckPasswordAsync(user, request.Password)) return Results.Unauthorized();
    var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, user.Id), new(ClaimTypes.Email, user.Email ?? request.Email) };
    claims.AddRange((await users.GetRolesAsync(user)).Select(role => new Claim(ClaimTypes.Role, role)));
    return Results.Ok(new { accessToken = JwtToken.Create(claims, jwt, signingKey), tokenType = "Bearer" });
});

var characters = app.MapGroup("/api/characters").RequireAuthorization();
characters.MapGet("/", async (ClaimsPrincipal principal, IGrainFactory grains) => Results.Ok(await grains.GetGrain<IAccountGrain>(principal.AccountId()).GetCharactersAsync()));
characters.MapPost("/", async (CreateCharacterRequest request, ClaimsPrincipal principal, IGrainFactory grains, IGameContentCatalog content) =>
{
    var result = await grains.GetGrain<IAccountGrain>(principal.AccountId()).CreateCharacterAsync(request.Name, content.GetActive().Version, request.ClassId);
    return result.Succeeded ? Results.Created($"/api/characters/{result.Character!.CharacterId}", result) : Results.BadRequest(result);
});
characters.MapPost("/{characterId}/enter", async (string characterId, EnterZoneRequest request, ClaimsPrincipal principal, IGrainFactory grains, IGameContentCatalog content) =>
{
    if (!await grains.GetGrain<IAccountGrain>(principal.AccountId()).OwnsCharacterAsync(characterId)) return Results.Forbid();
    var activeContent = content.GetActive();
    return Results.Ok(await grains.GetGrain<ICharacterGrain>(characterId).EnterZoneAsync(request.ZoneId, activeContent.Version));
});
characters.MapGet("/{characterId}", async (string characterId, ClaimsPrincipal principal, IGrainFactory grains) =>
{
    if (!await grains.GetGrain<IAccountGrain>(principal.AccountId()).OwnsCharacterAsync(characterId)) return Results.Forbid();
    return Results.Ok(await grains.GetGrain<ICharacterGrain>(characterId).GetSnapshotAsync());
});

app.MapPost("/api/admin/content/import", async (ContentImportRequest request, ClaimsPrincipal principal, ContentImportService importer, CancellationToken ct) => Results.Ok(await importer.ImportAsync(request.ContentJson, principal.Identity?.Name ?? principal.AccountId(), ct))).RequireAuthorization("ContentAdmin");
app.Map("/ws", WebSocketGameEndpoint.HandleAsync).RequireAuthorization();

app.Run();

public sealed record RegisterRequest(string Email, string Password);
public sealed record LoginRequest(string Email, string Password);
public sealed record CreateCharacterRequest(string Name, string? ClassId = null);
public sealed record EnterZoneRequest(string ZoneId);
public sealed record ContentImportRequest(string ContentJson);

public static class ClaimsPrincipalExtensions
{
    public static string AccountId(this ClaimsPrincipal principal) => principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? throw new UnauthorizedAccessException();
}

public static class JwtToken
{
    public static string Create(IEnumerable<Claim> claims, IConfigurationSection options, string signingKey)
    {
        var credentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)), SecurityAlgorithms.HmacSha256);
        return new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().WriteToken(new System.IdentityModel.Tokens.Jwt.JwtSecurityToken(options["Issuer"], options["Audience"], claims, expires: DateTime.UtcNow.AddHours(8), signingCredentials: credentials));
    }
}
