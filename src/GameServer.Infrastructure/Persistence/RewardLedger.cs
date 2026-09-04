using GameServer.Contracts;
using Microsoft.EntityFrameworkCore;

namespace GameServer.Infrastructure.Persistence;

public sealed class RewardLedger(IDbContextFactory<GameDbContext> database) : IRewardLedger
{
    public async Task<bool> TryRecordAsync(string characterId, string operationId, string kind, IReadOnlyList<RewardGrant> rewards, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(operationId)) throw new ArgumentException("An operation id is required for a reward.", nameof(operationId));
        if (rewards.Count == 0 || rewards.Any(x => string.IsNullOrWhiteSpace(x.ItemId) || x.Quantity <= 0)) throw new ArgumentException("At least one valid reward is required.", nameof(rewards));
        await using var db = await database.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            // 操作号作为跨 Grain 重试的持久化幂等键；内存状态丢失后仍能阻止重复发奖。
            if (await db.RewardOperations.AnyAsync(x => x.OperationId == operationId, cancellationToken)) return false;
            var now = DateTimeOffset.UtcNow;
            db.RewardOperations.Add(new RewardOperationEntity { OperationId = operationId, CharacterId = characterId, Kind = kind, CreatedAt = now });
            // 账本明细和物品投影必须在同一事务提交，保证审计记录与玩家可查询库存一致。
            foreach (var reward in rewards.GroupBy(x => x.ItemId, StringComparer.Ordinal).Select(x => new RewardGrant(x.Key, x.Sum(y => y.Quantity))))
            {
                db.LedgerEntries.Add(new LedgerEntry { OperationId = operationId, CharacterId = characterId, Kind = kind, ItemId = reward.ItemId, Quantity = reward.Quantity, CreatedAt = now });
                var projection = await db.PlayerItemProjections.FindAsync([characterId, reward.ItemId], cancellationToken);
                if (projection is null) db.PlayerItemProjections.Add(new PlayerItemProjection { CharacterId = characterId, ItemId = reward.ItemId, Quantity = reward.Quantity, UpdatedAt = now });
                else { projection.Quantity += reward.Quantity; projection.UpdatedAt = now; }
            }
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }
    }
}
