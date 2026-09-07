using GameServer.Gateway.Configuration;
using GameServer.Gateway.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace GameServer.Tests;

/// <summary>验证网关运行参数的默认值、配置源覆盖和启动期非法值诊断。</summary>
public sealed class GatewayConfigurationTests
{
    [Fact]
    public void Host_options_use_defaults_and_later_configuration_sources_win()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["GameServer:Realtime:MaxMessageBytes"] = "131072"
        });
        var services = new ServiceCollection();
        services.AddGameGateway(configuration);
        using var provider = services.BuildServiceProvider();

        Assert.Equal(131072, provider.GetRequiredService<IOptions<RealtimeOptions>>().Value.MaxMessageBytes);
        Assert.Equal(8192, provider.GetRequiredService<IOptions<RealtimeOptions>>().Value.ReceiveBufferBytes);
        Assert.Equal(TimeSpan.FromHours(8), provider.GetRequiredService<IOptions<JwtOptions>>().Value.AccessTokenLifetime);
        Assert.Equal("orleans-game", provider.GetRequiredService<IOptions<GameOrleansOptions>>().Value.ClusterId);
    }

    [Theory]
    [InlineData("GameServer:Realtime:MaxMessageBytes", "0")]
    [InlineData("GameServer:Realtime:ReceiveBufferBytes", "0")]
    [InlineData("GameServer:RateLimit:PermitLimit", "0")]
    [InlineData("GameServer:RateLimit:Window", "00:00:00")]
    [InlineData("Orleans:ClusterId", "")]
    public void Invalid_host_options_fail_validation(string key, string value)
    {
        var services = new ServiceCollection();
        services.AddGameGateway(BuildConfiguration(new Dictionary<string, string?> { [key] = value }));
        using var provider = services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() =>
        {
            if (key.StartsWith("GameServer:Realtime", StringComparison.Ordinal))
                _ = provider.GetRequiredService<IOptions<RealtimeOptions>>().Value;
            else if (key.StartsWith("GameServer:RateLimit", StringComparison.Ordinal))
                _ = provider.GetRequiredService<IOptions<GameRateLimitOptions>>().Value;
            else
                _ = provider.GetRequiredService<IOptions<GameOrleansOptions>>().Value;
        });
    }

    [Theory]
    [InlineData("Jwt:Issuer", "")]
    [InlineData("Jwt:Audience", "")]
    [InlineData("Jwt:SigningKey", "short")]
    [InlineData("Jwt:AccessTokenLifetime", "00:00:00")]
    [InlineData("Jwt:ClockSkew", "-00:00:01")]
    public void Invalid_jwt_options_fail_validation(string key, string value)
    {
        var services = new ServiceCollection();
        services.AddGameGateway(BuildConfiguration(new Dictionary<string, string?> { [key] = value }));
        using var provider = services.BuildServiceProvider();
        Assert.Throws<OptionsValidationException>(() => _ = provider.GetRequiredService<IOptions<JwtOptions>>().Value);
    }

    private static IConfiguration BuildConfiguration(IReadOnlyDictionary<string, string?> overrides)
    {
        var defaults = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] = "Host=localhost;Database=test;Username=test;Password=test",
            ["Jwt:Issuer"] = "issuer",
            ["Jwt:Audience"] = "audience",
            ["Jwt:SigningKey"] = "a-development-test-signing-key-with-32-bytes"
        };
        return new ConfigurationBuilder()
            .AddInMemoryCollection(defaults)
            .AddInMemoryCollection(overrides)
            .Build();
    }
}
