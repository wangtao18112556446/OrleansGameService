using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GameServer.Infrastructure.Persistence;

/// <summary>启动时应用版本化业务迁移，与 Orleans 系统表共享数据库且不清除已有数据。</summary>
public sealed class GameDatabaseInitializer(IServiceProvider services, ILogger<GameDatabaseInitializer> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<IDbContextFactory<GameDbContext>>();
        await using var context = await database.CreateDbContextAsync(cancellationToken);
        await context.Database.MigrateAsync(cancellationToken);
        logger.LogInformation("Game database schema is ready.");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
