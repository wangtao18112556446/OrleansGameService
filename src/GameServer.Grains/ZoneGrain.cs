using System.Security.Cryptography;
using System.Text;
using GameServer.Contracts;
using GameServer.Domain.Gameplay;
using GameServer.Grains.State;
using Orleans.Runtime;

namespace GameServer.Grains;

public sealed class ZoneGrain(
    [PersistentState("zone", "gameStore")] IPersistentState<ZoneState> state,
    IGameContentCatalog contentCatalog) : Grain, IZoneGrain
{
    public async Task<ZoneSnapshot> JoinAsync(string characterId, WorldPosition position, string contentVersion)
    {
        var logicalZoneId = LogicalZoneId();
        var content = contentCatalog.GetVersion(contentVersion);
        if (!content.Zones.TryGetValue(logicalZoneId, out var definition)) throw new InvalidOperationException("Unknown zone.");
        if (string.IsNullOrWhiteSpace(state.State.ContentVersion)) { state.State.ContentVersion = contentVersion; await EnsureMonstersAsync(); }
        if (!string.Equals(state.State.ContentVersion, contentVersion, StringComparison.Ordinal)) throw new InvalidOperationException("Zone content version is pinned until the next instance is created.");
        if (!state.State.Players.ContainsKey(characterId) && state.State.Players.Count >= definition.Capacity) throw new InvalidOperationException("Zone is full.");
        state.State.Players[characterId] = position;
        await state.WriteStateAsync();
        return await GetSnapshotAsync();
    }
    public async Task LeaveAsync(string characterId) { if (state.State.Players.Remove(characterId)) await state.WriteStateAsync(); }
    public async Task<bool> MoveAsync(string characterId, WorldPosition position) { if (!state.State.Players.ContainsKey(characterId)) return false; state.State.Players[characterId] = position; await state.WriteStateAsync(); return true; }
    public Task<bool> IsNearNpcAsync(string characterId, string npcId)
    {
        if (!state.State.Players.TryGetValue(characterId, out var player) || string.IsNullOrWhiteSpace(state.State.ContentVersion)) return Task.FromResult(false);
        var content = contentCatalog.GetVersion(state.State.ContentVersion);
        return Task.FromResult(content.Npcs.TryGetValue(npcId, out var npc) && npc.ZoneId == LogicalZoneId() && player.DistanceTo(npc.Position) <= (float)npc.InteractionRange);
    }
    public async Task<AttackResult> AttackMonsterAsync(string characterId, string monsterId, decimal damage, decimal range, string operationId)
    {
        if (!state.State.Players.TryGetValue(characterId, out var attacker)) return new AttackResult(false, "not_in_zone", false, null, 0, 0);
        var content = contentCatalog.GetVersion(state.State.ContentVersion);
        if (!content.Monsters.TryGetValue(monsterId, out var monster)) return new AttackResult(false, "unknown_target", false, null, 0, 0);
        var monsterGrain = GrainFactory.GetGrain<IMonsterGrain>(MonsterKey(monsterId));
        var target = await monsterGrain.GetSnapshotAsync();
        var decision = CombatRules.ValidateAttack(attacker, target.Position, range);
        if (!decision.Allowed) return new AttackResult(false, decision.ErrorCode, false, null, 0, target.Health);
        var result = await monsterGrain.ApplyDamageAsync(damage, operationId);
        if (!result.TargetDefeated) return result;
        var rewards = ResolveDrops(content, monster, operationId);
        return result with { RewardItemId = rewards.FirstOrDefault()?.ItemId, RewardCount = rewards.FirstOrDefault()?.Quantity ?? 0, Rewards = rewards };
    }
    public async Task<ZoneSnapshot> GetSnapshotAsync()
    {
        if (string.IsNullOrWhiteSpace(state.State.ContentVersion)) return new ZoneSnapshot(this.GetPrimaryKeyString(), string.Empty, []);
        var content = contentCatalog.GetVersion(state.State.ContentVersion);
        var monsters = await Task.WhenAll(content.Monsters.Keys.Select(id => GrainFactory.GetGrain<IMonsterGrain>(MonsterKey(id)).GetSnapshotAsync()));
        var players = state.State.Players.Select(x => new ZoneEntity(x.Key, "player", x.Value, 0, 0));
        var npcs = content.Npcs.Values.Where(x => x.ZoneId == LogicalZoneId()).Select(x => new ZoneEntity(x.Id, "npc", x.Position, 0, 0));
        return new ZoneSnapshot(this.GetPrimaryKeyString(), state.State.ContentVersion, players.Concat(monsters).Concat(npcs).ToArray());
    }
    private async Task EnsureMonstersAsync() { var content = contentCatalog.GetVersion(state.State.ContentVersion); await Task.WhenAll(content.Monsters.Values.Select(monster => GrainFactory.GetGrain<IMonsterGrain>(MonsterKey(monster.Id)).InitializeAsync(monster))); }
    private IReadOnlyList<InventoryStack> ResolveDrops(GameContent content, MonsterDefinition monster, string operationId)
    {
        if (monster.DropTableId is not { } tableId || !content.DropTables.TryGetValue(tableId, out var table)) return [new InventoryStack(monster.DropItemId, monster.DropCount)];
        return table.Entries.Where((entry, index) => Roll(operationId, index) < entry.Probability).GroupBy(x => x.ItemId, StringComparer.Ordinal).Select(x => new InventoryStack(x.Key, x.Sum(e => e.Quantity))).ToArray();
    }
    private static decimal Roll(string operationId, int index)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{operationId}:{index}"));
        return BitConverter.ToUInt32(hash, 0) / (decimal)uint.MaxValue;
    }
    private string LogicalZoneId() => this.GetPrimaryKeyString().Split(':', 2)[0];
    private string MonsterKey(string monsterId) => $"{this.GetPrimaryKeyString()}:{monsterId}";
}
