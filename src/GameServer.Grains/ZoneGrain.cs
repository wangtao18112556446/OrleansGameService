using System.Security.Cryptography;
using System.Text;
using GameServer.Abstractions;
using GameServer.Contracts;
using GameServer.Domain.Gameplay;
using GameServer.Grains.State;
using Orleans.Runtime;

namespace GameServer.Grains;

/// <summary>维护区域成员与内容版本，并按服务器位置校验攻击后转发给怪物。</summary>
public sealed class ZoneGrain(
    [PersistentState("zone", "gameStore")] IPersistentState<ZoneState> state,
    IGameContentCatalog contentCatalog) : Grain, IZoneGrain
{
    public async Task<ZoneSnapshot> JoinAsync(string characterId, WorldPosition position, string contentVersion)
    {
        await ReloadIfRequiredAsync();
        var logicalZoneId = LogicalZoneId();
        var content = contentCatalog.GetVersion(contentVersion).Content;
        if (!content.Zones.TryGetValue(logicalZoneId, out var definition)) throw new InvalidOperationException("Unknown zone.");
        // Zone 实例首次启用后固定内容版本，避免同一地图内玩家和怪物按不同配置结算。
        if (string.IsNullOrWhiteSpace(state.State.ContentVersion)) state.State.ContentVersion = contentVersion;
        if (!string.Equals(state.State.ContentVersion, contentVersion, StringComparison.Ordinal)) throw new InvalidOperationException("Zone content version is pinned until the next instance is created.");
        if (!state.State.Players.ContainsKey(characterId) && state.State.Players.Count >= definition.Capacity) throw new InvalidOperationException("Zone is full.");
        // 初始化调用可能在区域提交前失败；每次加入都幂等补齐怪物，避免内存中的版本标记跳过恢复。
        await EnsureMonstersAsync();
        state.State.Players[characterId] = position;
        await SaveAsync();
        return await GetSnapshotAsync();
    }
    public async Task LeaveAsync(string characterId) { await ReloadIfRequiredAsync(); if (state.State.Players.Remove(characterId)) await SaveAsync(); }
    public async Task<bool> MoveAsync(string characterId, WorldPosition position) { await ReloadIfRequiredAsync(); if (!state.State.Players.ContainsKey(characterId)) return false; state.State.Players[characterId] = position; await SaveAsync(); return true; }
    public async Task<bool> IsNearNpcAsync(string characterId, string npcId)
    {
        await ReloadIfRequiredAsync();
        if (!state.State.Players.TryGetValue(characterId, out var player) || string.IsNullOrWhiteSpace(state.State.ContentVersion)) return false;
        var content = contentCatalog.GetVersion(state.State.ContentVersion).Content;
        return content.Npcs.TryGetValue(npcId, out var npc) && npc.ZoneId == LogicalZoneId() && player.DistanceTo(npc.Position) <= (float)npc.InteractionRange;
    }
    public async Task<AttackResult> AttackMonsterAsync(string characterId, string monsterId, decimal damage, decimal range, string operationId)
    {
        await ReloadIfRequiredAsync();
        if (!state.State.Players.TryGetValue(characterId, out var attacker)) return new AttackResult(false, "not_in_zone", false, null, 0, 0);
        var content = contentCatalog.GetVersion(state.State.ContentVersion).Content;
        if (!content.Monsters.TryGetValue(monsterId, out var monster)) return new AttackResult(false, "unknown_target", false, null, 0, 0);
        var monsterGrain = GrainFactory.GetGrain<IMonsterGrain>(MonsterKey(monsterId));
        var target = await monsterGrain.GetSnapshotAsync();
        // 区域保存角色坐标，因此由区域在转发伤害前做权威距离校验。
        var decision = CombatRules.ValidateAttack(attacker, target.Position, range);
        if (!decision.Allowed) return new AttackResult(false, decision.ErrorCode, false, null, 0, target.Health);
        var scopedId = OperationIdentity.Scope(characterId, operationId);
        var result = await monsterGrain.ApplyDamageAsync(damage, scopedId);
        if (!result.TargetDefeated) return result;
        var rewards = ResolveDrops(content, monster, scopedId);
        return result with { RewardItemId = rewards.FirstOrDefault()?.ItemId, RewardCount = rewards.FirstOrDefault()?.Quantity ?? 0, Rewards = rewards };
    }
    public async Task<ZoneSnapshot> GetSnapshotAsync()
    {
        await ReloadIfRequiredAsync();
        if (string.IsNullOrWhiteSpace(state.State.ContentVersion)) return new ZoneSnapshot(this.GetPrimaryKeyString(), string.Empty, []);
        var content = contentCatalog.GetVersion(state.State.ContentVersion).Content;
        var monsters = await Task.WhenAll(content.Monsters.Keys.Select(id => GrainFactory.GetGrain<IMonsterGrain>(MonsterKey(id)).GetSnapshotAsync()));
        var players = state.State.Players.Select(x => new ZoneEntity(x.Key, "player", x.Value, 0, 0));
        var npcs = content.Npcs.Values.Where(x => x.ZoneId == LogicalZoneId()).Select(x => new ZoneEntity(x.Id, "npc", x.Position, 0, 0));
        return new ZoneSnapshot(this.GetPrimaryKeyString(), state.State.ContentVersion, players.Concat(monsters).Concat(npcs).ToArray());
    }
    private async Task EnsureMonstersAsync() { var content = contentCatalog.GetVersion(state.State.ContentVersion).Content; await Task.WhenAll(content.Monsters.Values.Select(monster => GrainFactory.GetGrain<IMonsterGrain>(MonsterKey(monster.Id)).InitializeAsync(monster))); }
    private IReadOnlyList<InventoryStack> ResolveDrops(GameContent content, MonsterDefinition monster, string operationId)
    {
        if (monster.DropTableId is not { } tableId || !content.DropTables.TryGetValue(tableId, out var table)) return [new InventoryStack(monster.DropItemId, monster.DropCount)];
        // 使用 operationId 派生确定性随机数：同一攻击重试时得到相同掉落，
        // 再交由角色奖励账本进行持久化去重。
        return table.Entries.Where((entry, index) => Roll(operationId, index) < entry.Probability).GroupBy(x => x.ItemId, StringComparer.Ordinal).Select(x => new InventoryStack(x.Key, x.Sum(e => e.Quantity))).ToArray();
    }
    private static decimal Roll(string operationId, int index)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{operationId}:{index}"));
        return BitConverter.ToUInt32(hash, 0) / (decimal)uint.MaxValue;
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
    private string LogicalZoneId() => this.GetPrimaryKeyString().Split(':', 2)[0];
    private string MonsterKey(string monsterId) => $"{this.GetPrimaryKeyString()}:{monsterId}";
}
