using GameServer.Abstractions;
using GameServer.Domain.Gameplay;
using GameServer.Infrastructure.Content;
using GameServer.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace GameServer.Infrastructure;

/// <summary>集中注册框架基础设施并返回可供示例游戏和二次开发包扩展的模块构建器。</summary>
public static class ServiceCollectionExtensions
{
    public static GameServerBuilder AddGameServer(this IServiceCollection services, IConfiguration configuration)
    {
        var postgres = configuration.GetConnectionString("Postgres")
            ?? throw new InvalidOperationException("ConnectionStrings:Postgres is required.");
        services.AddDbContextFactory<GameDbContext>(options => options.UseNpgsql(postgres));
        services.AddIdentityCore<IdentityUser>(options =>
        {
            options.Password.RequiredLength = 8;
            options.Password.RequireDigit = true;
            options.User.RequireUniqueEmail = true;
        }).AddRoles<IdentityRole>().AddEntityFrameworkStores<GameDbContext>();

        var redis = configuration.GetConnectionString("Redis");
        if (string.IsNullOrWhiteSpace(redis)) services.AddDistributedMemoryCache();
        else services.AddStackExchangeRedisCache(options =>
        {
            options.Configuration = redis;
            options.InstanceName = "orleans-game:";
        });

        services.AddOptions<GameContentOptions>()
            .Bind(configuration.GetSection(GameContentOptions.SectionName))
            .Validate(options => !string.IsNullOrWhiteSpace(options.BootstrapPath), "GameServer:Content:BootstrapPath is required.")
            .ValidateOnStart();

        var modules = new GameModuleCollection();
        services.AddSingleton(modules);
        services.AddSingleton<IGameplayFeatureCatalog>(provider =>
        {
            var moduleOrder = modules.Freeze()
                .Select((module, index) => (module.Id, index))
                .ToDictionary(entry => entry.Id, entry => entry.index, StringComparer.Ordinal);
            var registrations = modules.Effects
                .OrderBy(effect => moduleOrder[effect.ModuleId])
                .ThenBy(effect => effect.ImplementationType.FullName, StringComparer.Ordinal)
                .Select(effect => (effect.ModuleId, Effect: (IGameEffect)provider.GetRequiredService(effect.ImplementationType)))
                .ToArray();
            var duplicate = registrations.GroupBy(entry => entry.Effect.TypeId, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1);
            if (duplicate is not null)
                throw new InvalidOperationException($"Effect type '{duplicate.Key}' is registered by modules {string.Join(", ", duplicate.Select(entry => $"'{entry.ModuleId}'"))}.");
            return new GameplayFeatureCatalog(registrations.Select(entry => entry.Effect));
        });
        services.AddSingleton<JsonGameContentCompiler>();
        services.AddSingleton<IGameContentCatalog, JsonGameContentCatalog>();
        services.AddSingleton<ContentImportService>();
        services.AddSingleton<IRewardLedger, RewardLedger>();
        services.AddHostedService<GameModuleStartupValidator>();
        services.AddHostedService<GameDatabaseInitializer>();
        services.AddHostedService<ContentCatalogLoader>();

        return new GameServerBuilder(services, configuration, modules).AddCoreGameplay();
    }
}

/// <summary>在宿主接受流量前冻结模块图并强制构建效果目录，使配置冲突快速失败。</summary>
internal sealed class GameModuleStartupValidator(
    GameModuleCollection modules,
    IGameplayFeatureCatalog features) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = modules.Freeze();
        _ = features;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
