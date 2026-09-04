using GameServer.Contracts;
using GameServer.Grains.State;
using Orleans.Runtime;

namespace GameServer.Grains;

public sealed class MonsterGrain(
    [PersistentState("monster", "gameStore")] IPersistentState<MonsterState> state) : Grain, IMonsterGrain
{
    public async Task InitializeAsync(MonsterDefinition definition)
    {
        if (state.State.Definition is not null) return;
        state.State.Definition = definition;
        state.State.Health = definition.Health;
        await state.WriteStateAsync();
    }

    public async Task<AttackResult> ApplyDamageAsync(decimal damage, string operationId)
    {
        await RespawnIfDueAsync();
        if (state.State.Definition is null) return new AttackResult(false, "monster_unavailable", false, null, 0, 0);
        if (state.State.RespawnAt is not null) return new AttackResult(false, "monster_respawning", false, null, 0, 0);
        if (!string.IsNullOrWhiteSpace(operationId) && state.State.OperationResults.TryGetValue(operationId, out var previous)) return previous;
        if (!string.IsNullOrWhiteSpace(operationId)) state.State.ProcessedOperationIds.Add(operationId);

        state.State.Health = Math.Max(0, state.State.Health - Math.Max(0, damage));
        var defeated = state.State.Health == 0;
        if (defeated) state.State.RespawnAt = DateTimeOffset.UtcNow.AddSeconds(30);
        TrimOperations();
        await state.WriteStateAsync();
        var result = defeated
            ? new AttackResult(true, null, true, state.State.Definition.DropItemId, state.State.Definition.DropCount, 0)
            : new AttackResult(true, null, false, null, 0, state.State.Health);
        if (!string.IsNullOrWhiteSpace(operationId)) state.State.OperationResults[operationId] = result;
        await state.WriteStateAsync();
        return result;
    }

    public async Task<ZoneEntity> GetSnapshotAsync()
    {
        await RespawnIfDueAsync();
        if (state.State.Definition is null) return new ZoneEntity(this.GetPrimaryKeyString(), "monster", new WorldPosition(0, 0), 0, 0);
        return new ZoneEntity(state.State.Definition.Id, "monster", state.State.Definition.SpawnPosition, state.State.Health, state.State.Definition.Health);
    }

    private async Task RespawnIfDueAsync()
    {
        if (state.State.Definition is null || state.State.RespawnAt is not { } at || at > DateTimeOffset.UtcNow) return;
        state.State.Health = state.State.Definition.Health;
        state.State.RespawnAt = null;
        await state.WriteStateAsync();
    }

    private void TrimOperations()
    {
        if (state.State.ProcessedOperationIds.Count <= 512) return;
        var retained = state.State.ProcessedOperationIds.TakeLast(512).ToHashSet(StringComparer.Ordinal);
        state.State.ProcessedOperationIds = retained;
        state.State.OperationResults = state.State.OperationResults.Where(x => retained.Contains(x.Key)).ToDictionary();
    }
}
