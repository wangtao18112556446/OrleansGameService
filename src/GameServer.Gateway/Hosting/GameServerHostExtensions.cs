using System.Text;
using System.Threading.RateLimiting;
using GameServer.Abstractions;
using GameServer.Gateway.Configuration;
using GameServer.Gateway.Endpoints;
using GameServer.Infrastructure;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.IdentityModel.Tokens;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Orleans.Configuration;
using Orleans.Hosting;

namespace GameServer.Gateway.Hosting;

/// <summary>组合网关依赖、强类型配置、Orleans 宿主和固定的请求管线顺序。</summary>
public static class GameServerHostExtensions
{
    public static GameServerBuilder AddGameGateway(this IServiceCollection services, IConfiguration configuration)
    {
        var jwt = configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();
        var rateLimit = configuration.GetSection(GameRateLimitOptions.SectionName).Get<GameRateLimitOptions>() ?? new GameRateLimitOptions();

        services.AddOptions<JwtOptions>()
            .Bind(configuration.GetSection(JwtOptions.SectionName))
            .Validate(options => !string.IsNullOrWhiteSpace(options.Issuer), "Jwt:Issuer is required.")
            .Validate(options => !string.IsNullOrWhiteSpace(options.Audience), "Jwt:Audience is required.")
            .Validate(options => Encoding.UTF8.GetByteCount(options.SigningKey ?? string.Empty) >= 32, "Jwt:SigningKey must contain at least 32 UTF-8 bytes.")
            .Validate(options => options.AccessTokenLifetime > TimeSpan.Zero, "Jwt:AccessTokenLifetime must be positive.")
            .Validate(options => options.ClockSkew >= TimeSpan.Zero, "Jwt:ClockSkew cannot be negative.")
            .ValidateOnStart();
        services.AddOptions<GameOrleansOptions>()
            .Bind(configuration.GetSection(GameOrleansOptions.SectionName))
            .Validate(options => !string.IsNullOrWhiteSpace(options.ClusterId), "Orleans:ClusterId is required.")
            .Validate(options => !string.IsNullOrWhiteSpace(options.ServiceId), "Orleans:ServiceId is required.")
            .ValidateOnStart();
        services.AddOptions<RealtimeOptions>()
            .Bind(configuration.GetSection(RealtimeOptions.SectionName))
            .Validate(options => options.MaxMessageBytes > 0, "GameServer:Realtime:MaxMessageBytes must be positive.")
            .Validate(options => options.ReceiveBufferBytes > 0 && options.ReceiveBufferBytes <= options.MaxMessageBytes, "Realtime receive buffer must be positive and no larger than the message limit.")
            .ValidateOnStart();
        services.AddOptions<GameRateLimitOptions>()
            .Bind(configuration.GetSection(GameRateLimitOptions.SectionName))
            .Validate(options => options.PermitLimit > 0, "GameServer:RateLimit:PermitLimit must be positive.")
            .Validate(options => options.Window > TimeSpan.Zero, "GameServer:RateLimit:Window must be positive.")
            .ValidateOnStart();

        services.AddSingleton(TimeProvider.System);
        services.AddProblemDetails();
        services.AddHealthChecks();
        services.AddAuthorization(options => options.AddPolicy("ContentAdmin", policy => policy.RequireRole("content-admin")));
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
        {
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = jwt.Issuer,
                ValidateAudience = true,
                ValidAudience = jwt.Audience,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)),
                ValidateLifetime = true,
                ClockSkew = jwt.ClockSkew
            };
        });
        services.AddRateLimiter(options => options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = rateLimit.PermitLimit,
                    Window = rateLimit.Window,
                    QueueLimit = 0
                })));
        services.AddOpenTelemetry()
            .WithTracing(tracing => tracing.AddAspNetCoreInstrumentation())
            .WithMetrics(metrics => metrics.AddAspNetCoreInstrumentation().AddRuntimeInstrumentation());

        return services.AddGameServer(configuration);
    }

    public static IHostBuilder UseGameOrleans(this IHostBuilder host, IConfiguration configuration)
    {
        var configured = configuration.GetSection(GameOrleansOptions.SectionName).Get<GameOrleansOptions>() ?? new GameOrleansOptions();
        return host.UseOrleans(silo =>
        {
            silo.Configure<ClusterOptions>(options =>
            {
                options.ClusterId = configured.ClusterId;
                options.ServiceId = configured.ServiceId;
            });
            if (configured.UseAdoNet)
            {
                var postgres = configuration.GetConnectionString("Postgres")
                    ?? throw new InvalidOperationException("ConnectionStrings:Postgres is required when Orleans:UseAdoNet is true.");
                silo.UseAdoNetClustering(options =>
                {
                    options.Invariant = "Npgsql";
                    options.ConnectionString = postgres;
                });
                silo.AddAdoNetGrainStorage("gameStore", options =>
                {
                    options.Invariant = "Npgsql";
                    options.ConnectionString = postgres;
                });
            }
            else
            {
                silo.UseLocalhostClustering();
                silo.AddMemoryGrainStorage("gameStore");
            }
        });
    }

    public static WebApplication UseGameGateway(this WebApplication app)
    {
        app.UseExceptionHandler();
        app.UseHttpsRedirection();
        app.UseRateLimiter();
        app.UseWebSockets();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGameEndpoints();
        return app;
    }
}
