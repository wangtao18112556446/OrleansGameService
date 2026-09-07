using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace GameServer.Infrastructure.Persistence;

/// <summary>为迁移工具创建上下文，生成迁移时无需启动 Gateway 或 Orleans。</summary>
public sealed class GameDbContextFactory : IDesignTimeDbContextFactory<GameDbContext>
{
    public GameDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<GameDbContext>()
        .UseNpgsql(Environment.GetEnvironmentVariable("ConnectionStrings__Postgres")
            ?? "Host=localhost;Database=orleans_game;Username=game;Password=game").Options);
}
