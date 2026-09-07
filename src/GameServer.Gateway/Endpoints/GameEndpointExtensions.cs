using System.Security.Claims;
using System.Text;
using GameServer.Abstractions;
using GameServer.Contracts;
using GameServer.Gateway.Configuration;
using GameServer.Gateway.Realtime;
using GameServer.Infrastructure.Content;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Orleans;

namespace GameServer.Gateway.Endpoints;

/// <summary>映射现有健康、鉴权、角色、内容管理和实时连接端点。</summary>
public static class GameEndpointExtensions
{
    public static IEndpointRouteBuilder MapGameEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/health", () => Results.Ok(new { status = "ok" }));
        endpoints.MapHealthChecks("/health/ready");

        var auth = endpoints.MapGroup("/api/auth");
        auth.MapPost("/register", async (RegisterRequest request, UserManager<IdentityUser> users) =>
        {
            var user = new IdentityUser { UserName = request.Email, Email = request.Email };
            var result = await users.CreateAsync(user, request.Password);
            return result.Succeeded
                ? Results.Created($"/api/accounts/{user.Id}", new { user.Id, user.Email })
                : Results.ValidationProblem(result.Errors.GroupBy(error => error.Code).ToDictionary(group => group.Key, group => group.Select(error => error.Description).ToArray()));
        });
        auth.MapPost("/login", async (
            LoginRequest request,
            UserManager<IdentityUser> users,
            IOptions<JwtOptions> jwt,
            TimeProvider timeProvider) =>
        {
            var user = await users.FindByEmailAsync(request.Email);
            if (user is null || !await users.CheckPasswordAsync(user, request.Password)) return Results.Unauthorized();
            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, user.Id),
                new(ClaimTypes.Email, user.Email ?? request.Email)
            };
            claims.AddRange((await users.GetRolesAsync(user)).Select(role => new Claim(ClaimTypes.Role, role)));
            return Results.Ok(new
            {
                accessToken = JwtToken.Create(claims, jwt.Value, timeProvider),
                tokenType = "Bearer"
            });
        });

        var characters = endpoints.MapGroup("/api/characters").RequireAuthorization();
        characters.MapGet("/", async (ClaimsPrincipal principal, IGrainFactory grains)
            => Results.Ok(await grains.GetGrain<IAccountGrain>(principal.AccountId()).GetCharactersAsync()));
        characters.MapPost("/", async (
            CreateCharacterRequest request,
            ClaimsPrincipal principal,
            IGrainFactory grains,
            IGameContentCatalog content) =>
        {
            var result = await grains.GetGrain<IAccountGrain>(principal.AccountId())
                .CreateCharacterAsync(request.Name, content.GetActive().Version, request.ClassId);
            return result.Succeeded
                ? Results.Created($"/api/characters/{result.Character!.CharacterId}", result)
                : Results.BadRequest(result);
        });
        characters.MapPost("/{characterId}/enter", async (
            string characterId,
            EnterZoneRequest request,
            ClaimsPrincipal principal,
            IGrainFactory grains,
            IGameContentCatalog content) =>
        {
            if (!await grains.GetGrain<IAccountGrain>(principal.AccountId()).OwnsCharacterAsync(characterId)) return Results.Forbid();
            return Results.Ok(await grains.GetGrain<ICharacterGrain>(characterId).EnterZoneAsync(request.ZoneId, content.GetActive().Version));
        });
        characters.MapGet("/{characterId}", async (string characterId, ClaimsPrincipal principal, IGrainFactory grains) =>
        {
            if (!await grains.GetGrain<IAccountGrain>(principal.AccountId()).OwnsCharacterAsync(characterId)) return Results.Forbid();
            return Results.Ok(await grains.GetGrain<ICharacterGrain>(characterId).GetSnapshotAsync());
        });

        endpoints.MapPost("/api/admin/content/import", async (
                ContentImportRequest request,
                ClaimsPrincipal principal,
                ContentImportService importer,
                CancellationToken cancellationToken)
            => Results.Ok(await importer.ImportAsync(request.ContentJson, principal.Identity?.Name ?? principal.AccountId(), cancellationToken)))
            .RequireAuthorization("ContentAdmin");
        endpoints.Map("/ws", WebSocketGameEndpoint.HandleAsync).RequireAuthorization();
        return endpoints;
    }
}

/// <summary>承载账号注册请求。</summary>
public sealed record RegisterRequest(string Email, string Password);

/// <summary>承载账号密码登录请求。</summary>
public sealed record LoginRequest(string Email, string Password);

/// <summary>承载角色创建请求及可选职业。</summary>
public sealed record CreateCharacterRequest(string Name, string? ClassId = null);

/// <summary>承载角色进入逻辑区域的请求。</summary>
public sealed record EnterZoneRequest(string ZoneId);

/// <summary>承载管理员导入的原始版本化内容包。</summary>
public sealed record ContentImportRequest(string ContentJson);

/// <summary>集中读取认证主体中的权威账号标识。</summary>
public static class ClaimsPrincipalExtensions
{
    public static string AccountId(this ClaimsPrincipal principal)
        => principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? throw new UnauthorizedAccessException();
}

/// <summary>按照已验证的强类型配置签发短期 JWT。</summary>
public static class JwtToken
{
    public static string Create(IEnumerable<Claim> claims, JwtOptions options, TimeProvider timeProvider)
    {
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.SigningKey)),
            SecurityAlgorithms.HmacSha256);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var token = new System.IdentityModel.Tokens.Jwt.JwtSecurityToken(
            options.Issuer,
            options.Audience,
            claims,
            notBefore: now,
            expires: now.Add(options.AccessTokenLifetime),
            signingCredentials: credentials);
        return new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().WriteToken(token);
    }
}
