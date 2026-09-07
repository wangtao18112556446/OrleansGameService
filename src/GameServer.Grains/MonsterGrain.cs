using GameServer.Contracts;
using GameServer.Grains.State;
using GameServer.Domain.Gameplay;
using Orleans.Runtime;

namespace GameServer.Grains;

/// <summary>原子保存怪物伤害和攻击回执，使调用方可在故障恢复时安全重试。</summary>
public sealed class MonsterGrain(
    [PersistentState("monster", "gameStore")] IPersistentState<MonsterState> state) : Grain, IMonsterGrain
{
    public async Task InitializeAsync(MonsterDefinition definition)
    {
        await ReloadIfRequiredAsync();
        if (state.State.Definition is not null) return;
        state.State.Definition = definition;
        state.State.Health = definition.Health;
        await SaveAsync();
    }

    public async Task<AttackResult> ApplyDamageAsync(decimal damage, string operationId)
    {
        await ReloadIfRequiredAsync();
        if (!OperationIdentity.IsValid(operationId)) return new AttackResult(false, "invalid_operation_id", false, null, 0, state.State.Health);
        // 即使怪物已经死亡或重生，旧操作也必须返回原始结果。
        if (state.State.OperationResults.TryGetValue(operationId, out var previous)) return previous;
        await RespawnIfDueAsync();
        if (state.State.Definition is null) return new AttackResult(false, "monster_unavailable", false, null, 0, 0);
        if (state.State.RespawnAt is not null) return new AttackResult(false, "monster_respawning", false, null, 0, 0);
        if (state.State.ProcessedOperationIds.Contains(operationId)) return new AttackResult(false, "operation_expired", false, null, 0, state.State.Health);

        state.State.Health = Math.Max(0, state.State.Health - Math.Max(0, damage));
        var defeated = state.State.Health == 0;
        // 怪物死亡即进入不可攻击的重生窗口，防止并发攻击在同一击杀上重复结算。
        if (defeated) state.State.RespawnAt = DateTimeOffset.UtcNow.AddSeconds(30);
        var result = defeated
            ? new AttackResult(true, null, true, state.State.Definition.DropItemId, state.State.Definition.DropCount, 0)
            : new AttackResult(true, null, false, null, 0, state.State.Health);
        state.State.OperationResults[operationId] = result;
        await SaveAsync();
        return result;
    }

    public async Task<ZoneEntity> GetSnapshotAsync()
    {
        await ReloadIfRequiredAsync();
        await RespawnIfDueAsync();
        if (state.State.Definition is null) return new ZoneEntity(this.GetPrimaryKeyString(), "monster", new WorldPosition(0, 0), 0, 0);
        return new ZoneEntity(state.State.Definition.Id, "monster", state.State.Definition.SpawnPosition, state.State.Health, state.State.Definition.Health);
    }

    private async Task RespawnIfDueAsync()
    {
        if (state.State.Definition is null || state.State.RespawnAt is not { } at || at > DateTimeOffset.UtcNow) return;
        state.State.Health = state.State.Definition.Health;
        state.State.RespawnAt = null;
        await SaveAsync();
    }

    private bool reloadRequired;

    private async Task ReloadIfRequiredAsync()
    {
        if (reloadRequired) { await state.ReadStateAsync(); reloadRequired = false; }
    }

    private async Task SaveAsync()
    {
        try { await state.WriteStateAsync(); }
        catch { reloadRequired = true; throw; }
    }
}
