using GameServer.Contracts;
using GameServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Npgsql;

namespace GameServer.Tests;

/// <summary>针对 Compose PostgreSQL 验证迁移基线、并发幂等与审计投影；每个测试使用独立临时数据库。</summary>
public sealed class PostgresPersistenceTests
{
    [PostgresFact]
    public async Task Migrations_create_game_tables_beside_existing_orleans_tables_and_restart_safely()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.Factory.CreateDbContext();
        await db.Database.ExecuteSqlRawAsync("CREATE TABLE orleans_fixture (id integer PRIMARY KEY); INSERT INTO orleans_fixture VALUES (1);");
        await db.Database.MigrateAsync();
        db.ContentSettings.Add(new ContentSettingsEntity { Id = 1, ActiveVersion = "v1" });
        await db.SaveChangesAsync();
        await db.Database.MigrateAsync();
        Assert.Single(await db.Database.GetAppliedMigrationsAsync());
        Assert.Equal("v1", await db.ContentSettings.Select(x => x.ActiveVersion).SingleAsync());
        Assert.Equal(1, await db.Database.SqlQueryRaw<int>("SELECT id AS \"Value\" FROM orleans_fixture").SingleAsync());
        Assert.Empty(await db.Users.ToListAsync());
    }

    [PostgresFact]
    public async Task Baseline_adopts_existing_mvp_schema_without_losing_data()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.Factory.CreateDbContext();
        await db.Database.EnsureCreatedAsync();
        db.ContentSettings.Add(new ContentSettingsEntity { Id = 1, ActiveVersion = "legacy" });
        db.PlayerItemProjections.Add(new PlayerItemProjection { CharacterId = "legacy-player", ItemId = "gel", Quantity = 12, UpdatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        await db.Database.MigrateAsync();
        Assert.Equal("legacy", await db.ContentSettings.Select(x => x.ActiveVersion).SingleAsync());
        Assert.Equal(12, await db.PlayerItemProjections.Select(x => x.Quantity).SingleAsync());
    }

    [PostgresFact]
    public async Task Ledger_deduplicates_concurrent_deliveries_and_isolates_characters()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.Factory.CreateDbContext();
        await db.Database.MigrateAsync();
        var ledger = new RewardLedger(database.Factory);
        var rewards = new[] { new RewardGrant("gel", 2) };
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => ledger.TryRecordAsync("hero", "op", "drop", rewards)));
        Assert.Single(outcomes, x => x);
        Assert.True(await ledger.TryRecordAsync("other-hero", "op", "drop", rewards));
        await Task.WhenAll(Enumerable.Range(0, 8).Select(i => ledger.TryRecordAsync("hero", $"distinct-{i}", "drop", rewards)));
        Assert.Equal(18, await db.PlayerItemProjections.Where(x => x.CharacterId == "hero").Select(x => x.Quantity).SingleAsync());
        Assert.Equal(10, await db.RewardOperations.CountAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => ledger.TryRecordAsync("hero", "op", "drop", [new RewardGrant("gel", 3)]));
    }

    [PostgresFact]
    public async Task Ledger_database_failure_is_not_reported_as_duplicate()
    {
        await using var database = await TestDatabase.CreateAsync();
        // 未迁移数据库的缺表错误必须向上传播，调用方才会保留待投递奖励。
        var ledger = new RewardLedger(database.Factory);
        await Assert.ThrowsAsync<PostgresException>(() => ledger.TryRecordAsync("hero", "op", "drop", [new RewardGrant("gel", 1)]));
    }

    /// <summary>只在显式配置 Compose PostgreSQL 连接时运行，缺少依赖时在测试报告中明确跳过。</summary>
    public sealed class PostgresFactAttribute : FactAttribute
    {
        public PostgresFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GameTests__Postgres")))
                Skip = "Set GameTests__Postgres to the Docker Compose PostgreSQL connection.";
        }
    }

    /// <summary>创建带随机名称的测试数据库，清理时只删除自己创建的数据库。</summary>
    private sealed class TestDatabase(string connectionString, string name, IDbContextFactory<GameDbContext> factory) : IAsyncDisposable
    {
        public IDbContextFactory<GameDbContext> Factory { get; } = factory;
        public static async Task<TestDatabase> CreateAsync()
        {
            var connectionString = Environment.GetEnvironmentVariable("GameTests__Postgres")!;
            var name = "game_test_" + Guid.NewGuid().ToString("N");
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", connection);
            await command.ExecuteNonQueryAsync();
            var target = new NpgsqlConnectionStringBuilder(connectionString) { Database = name, Pooling = false };
            var factory = new PooledDbContextFactory<GameDbContext>(new DbContextOptionsBuilder<GameDbContext>().UseNpgsql(target.ConnectionString).Options);
            return new TestDatabase(connectionString, name, factory);
        }
        public async ValueTask DisposeAsync()
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"DROP DATABASE \"{name}\" WITH (FORCE)", connection);
            await command.ExecuteNonQueryAsync();
        }
    }
}
