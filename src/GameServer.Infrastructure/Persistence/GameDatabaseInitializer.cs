using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GameServer.Infrastructure.Persistence;

public sealed class GameDatabaseInitializer(IServiceProvider services, ILogger<GameDatabaseInitializer> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<IDbContextFactory<GameDbContext>>();
        await using var context = await database.CreateDbContextAsync(cancellationToken);
        await context.Database.EnsureCreatedAsync(cancellationToken);
        await context.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "ContentSettings" (
                "Id" integer NOT NULL PRIMARY KEY,
                "ActiveVersion" character varying NOT NULL
            );
            CREATE TABLE IF NOT EXISTS "RewardOperations" (
                "OperationId" character varying NOT NULL PRIMARY KEY,
                "CharacterId" character varying NOT NULL,
                "Kind" character varying NOT NULL,
                "CreatedAt" timestamp with time zone NOT NULL
            );
            DROP INDEX IF EXISTS "IX_LedgerEntries_OperationId";
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_LedgerEntries_OperationId_ItemId" ON "LedgerEntries" ("OperationId", "ItemId");
            """, cancellationToken);
        logger.LogInformation("Game database schema is ready.");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
