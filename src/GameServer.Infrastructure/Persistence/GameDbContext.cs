using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace GameServer.Infrastructure.Persistence;

public sealed class GameDbContext(DbContextOptions<GameDbContext> options) : IdentityDbContext<IdentityUser>(options)
{
    public DbSet<ContentReleaseEntity> ContentReleases => Set<ContentReleaseEntity>();
    public DbSet<ContentSettingsEntity> ContentSettings => Set<ContentSettingsEntity>();
    public DbSet<RewardOperationEntity> RewardOperations => Set<RewardOperationEntity>();
    public DbSet<LedgerEntry> LedgerEntries => Set<LedgerEntry>();
    public DbSet<PlayerItemProjection> PlayerItemProjections => Set<PlayerItemProjection>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.Entity<ContentReleaseEntity>().HasKey(x => x.Version);
        builder.Entity<ContentSettingsEntity>().HasKey(x => x.Id);
        builder.Entity<RewardOperationEntity>().HasKey(x => x.OperationId);
        builder.Entity<LedgerEntry>().HasIndex(x => new { x.OperationId, x.ItemId }).IsUnique();
        builder.Entity<LedgerEntry>().Property(x => x.Kind).HasMaxLength(64);
        builder.Entity<PlayerItemProjection>().HasKey(x => new { x.CharacterId, x.ItemId });
    }
}

public sealed class ContentReleaseEntity
{
    public required string Version { get; set; }
    public required string ContentJson { get; set; }
    public required string ImportedBy { get; set; }
    public DateTimeOffset ImportedAt { get; set; }
}

public sealed class ContentSettingsEntity
{
    public int Id { get; set; }
    public required string ActiveVersion { get; set; }
}

public sealed class RewardOperationEntity
{
    public required string OperationId { get; set; }
    public required string CharacterId { get; set; }
    public required string Kind { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class LedgerEntry
{
    public long Id { get; set; }
    public required string OperationId { get; set; }
    public required string CharacterId { get; set; }
    public required string Kind { get; set; }
    public required string ItemId { get; set; }
    public int Quantity { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class PlayerItemProjection
{
    public required string CharacterId { get; set; }
    public required string ItemId { get; set; }
    public int Quantity { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
