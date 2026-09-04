using GameServer.Contracts;
using GameServer.Infrastructure.Content;
using GameServer.Infrastructure.Persistence;
using GameServer.Domain.Gameplay;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;

namespace GameServer.Infrastructure;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddGameInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var postgres = configuration.GetConnectionString("Postgres") ?? throw new InvalidOperationException("ConnectionStrings:Postgres is required.");
        services.AddDbContextFactory<GameDbContext>(options => options.UseNpgsql(postgres));
        services.AddIdentityCore<IdentityUser>(options =>
        {
            options.Password.RequiredLength = 8;
            options.Password.RequireDigit = true;
            options.User.RequireUniqueEmail = true;
        }).AddRoles<IdentityRole>().AddEntityFrameworkStores<GameDbContext>();

        var redis = configuration.GetConnectionString("Redis");
        if (string.IsNullOrWhiteSpace(redis)) services.AddDistributedMemoryCache();
        else services.AddStackExchangeRedisCache(options => { options.Configuration = redis; options.InstanceName = "orleans-game:"; });

        services.AddSingleton(GameplayCore.CreateRegistry());
        services.AddSingleton<IGameContentCatalog, JsonGameContentCatalog>();
        services.AddSingleton<ContentImportService>();
        services.AddSingleton<IRewardLedger, RewardLedger>();
        services.AddHostedService<GameDatabaseInitializer>();
        services.AddHostedService<ContentCatalogLoader>();
        return services;
    }
}
