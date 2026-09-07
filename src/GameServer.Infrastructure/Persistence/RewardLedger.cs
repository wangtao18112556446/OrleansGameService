using GameServer.Contracts;
using GameServer.Domain.Gameplay;
using Microsoft.EntityFrameworkCore;

namespace GameServer.Infrastructure.Persistence;

/// <summary>幂等接收角色奖励发件箱，保存审计流水和累计奖励投影；角色状态仍是可用库存的权威。</summary>
public sealed class RewardLedger(IDbContextFactory<GameDbContext> database) : IRewardLedger
{
    public async Task<bool> TryRecordAsync(string characterId, string operationId, string kind, IReadOnlyList<RewardGrant> rewards, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(characterId) || !OperationIdentity.IsValid(operationId)) throw new ArgumentException("A character and valid operation id are required.");
        if (string.IsNullOrWhiteSpace(kind) || kind.Length > 64 || rewards.Count == 0 || rewards.Any(x => string.IsNullOrWhiteSpace(x.ItemId) || x.Quantity <= 0))
            throw new ArgumentException("A valid kind and positive rewards are required.");
        var grants = rewards.GroupBy(x => x.ItemId, StringComparer.Ordinal).Select(x => new RewardGrant(x.Key, x.Sum(y => y.Quantity))).OrderBy(x => x.ItemId, StringComparer.Ordinal).ToArray();
        var scopedId = OperationIdentity.Scope(characterId, operationId);
        await using var db = await database.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        // 同一角色的并发投递串行更新投影，避免两个不同操作同时读旧数量而丢失增量。
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({characterId}, 0))", cancellationToken);
        var previous = await db.RewardOperations.SingleOrDefaultAsync(x => x.OperationId == scopedId, cancellationToken)
            ?? await db.RewardOperations.SingleOrDefaultAsync(x => x.OperationId == operationId && x.CharacterId == characterId, cancellationToken);
        if (previous is not null)
        {
            // 兼容旧版未作用域化的账本；只接受角色、业务种类和奖励明细完全一致的重投。
            var entries = await db.LedgerEntries.Where(x => x.OperationId == previous.OperationId).ToListAsync(cancellationToken);
            var recorded = entries.Select(x => new RewardGrant(x.ItemId, x.Quantity)).OrderBy(x => x.ItemId, StringComparer.Ordinal);
            if (previous.CharacterId != characterId || previous.Kind != kind || !recorded.SequenceEqual(grants))
                throw new InvalidOperationException("Reward operation conflicts with its recorded payload.");
            return false;
        }
        var now = DateTimeOffset.UtcNow;
        db.RewardOperations.Add(new RewardOperationEntity { OperationId = scopedId, CharacterId = characterId, Kind = kind, CreatedAt = now });
        foreach (var reward in grants)
        {
            db.LedgerEntries.Add(new LedgerEntry { OperationId = scopedId, CharacterId = characterId, Kind = kind, ItemId = reward.ItemId, Quantity = reward.Quantity, CreatedAt = now });
            var projection = await db.PlayerItemProjections.FindAsync([characterId, reward.ItemId], cancellationToken);
            if (projection is null) db.PlayerItemProjections.Add(new PlayerItemProjection { CharacterId = characterId, ItemId = reward.ItemId, Quantity = reward.Quantity, UpdatedAt = now });
            else { projection.Quantity = checked(projection.Quantity + reward.Quantity); projection.UpdatedAt = now; }
        }
        // 存储错误不再伪装成重复操作；调用方保留发件箱并在恢复后重新投递。
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }
}
