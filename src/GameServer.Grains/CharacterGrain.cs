using GameServer.Contracts;
using GameServer.Domain.Attributes;
using GameServer.Domain.Gameplay;
using GameServer.Grains.State;
using Orleans.Runtime;

namespace GameServer.Grains;

public sealed class CharacterGrain(
    [PersistentState("character", "gameStore")] IPersistentState<CharacterState> state,
    IGameContentCatalog contentCatalog,
    IRewardLedger rewardLedger,
    GameFeatureRegistry features) : Grain, ICharacterGrain
{
    public async Task InitializeAsync(string accountId, string name, string contentVersion, string classId)
    {
        if (!string.IsNullOrEmpty(state.State.AccountId)) return;
        var content = contentCatalog.GetVersion(contentVersion);
        if (!content.Classes.TryGetValue(classId, out var characterClass)) throw new InvalidOperationException("Unknown character class.");
        state.State.AccountId = accountId;
        state.State.Name = name;
        state.State.ClassId = classId;
        state.State.ContentVersion = content.Version;
        state.State.BaseAttributes = content.Attributes.ToDictionary(x => x.Key, x => characterClass.InitialAttributes.GetValueOrDefault(x.Key, x.Value.DefaultValue), StringComparer.Ordinal);
        state.State.LearnedSkillIds = characterClass.InitialSkillIds.ToHashSet(StringComparer.Ordinal);
        foreach (var item in characterClass.InitialItems) state.State.Inventory[item.ItemId] = state.State.Inventory.GetValueOrDefault(item.ItemId) + item.Quantity;
        NormalizeResources(content);
        await state.WriteStateAsync();
    }

    public Task<CharacterSnapshot> GetSnapshotAsync() => Task.FromResult(Snapshot());
    public Task<CharacterSnapshotV2> GetSnapshotV2Async() => Task.FromResult(SnapshotV2());

    public async Task<CommandResult> EnterZoneAsync(string zoneId, string contentVersion)
    {
        var content = contentCatalog.GetVersion(contentVersion);
        if (!content.Zones.ContainsKey(zoneId)) return Failure("invalid_zone");
        // 先退出旧实例再切换，确保一个角色不会同时留在两个 Zone Grain 的玩家列表中。
        if (!string.IsNullOrWhiteSpace(state.State.ZoneId)) await GrainFactory.GetGrain<IZoneGrain>(state.State.ZoneId).LeaveAsync(this.GetPrimaryKeyString());
        state.State.ZoneId = ZoneKey(zoneId, contentVersion);
        state.State.ContentVersion = contentVersion;
        state.State.Position = new WorldPosition(0, 0);
        try { await GrainFactory.GetGrain<IZoneGrain>(state.State.ZoneId).JoinAsync(this.GetPrimaryKeyString(), state.State.Position, contentVersion); }
        catch (InvalidOperationException) { return Failure("zone_unavailable"); }
        NormalizeResources(content);
        await state.WriteStateAsync();
        return Success();
    }

    public async Task<CommandResult> MoveAsync(MoveCommand command, string operationId)
    {
        if (WasProcessed(operationId)) return Success();
        var next = new WorldPosition(command.X, command.Y);
        if (!CombatRules.ValidateMove(state.State.Position, next, 12f).Allowed) return Failure("invalid_movement");
        if (!await Zone().MoveAsync(this.GetPrimaryKeyString(), next)) return Failure("not_in_zone");
        state.State.Position = next;
        CompleteOperation(operationId);
        await state.WriteStateAsync();
        return Success();
    }

    public async Task<CommandResult> AttackAsync(AttackCommand command, string operationId)
    {
        // 客户端重传同一操作时直接返回成功；奖励幂等还会在持久化账本中再次兜底。
        if (WasProcessed(operationId)) return Success();
        if (state.State.LastAttackAt is { } last && DateTimeOffset.UtcNow - last < TimeSpan.FromMilliseconds(500)) return Failure("attack_on_cooldown");
        var content = Content();
        if (!content.Monsters.TryGetValue(command.TargetMonsterId, out var monster)) return Failure("unknown_target");
        // 先按最坏情况下的掉落预检背包。这样击杀后不会出现奖励已生成却无处存放的半完成状态。
        var potential = PotentialDrops(content, monster);
        if (!InventoryRules.CanReceive(state.State.Inventory, potential, content.Items)) return Failure("inventory_full");
        var attributes = Attributes(content);
        var attack = await Zone().AttackMonsterAsync(this.GetPrimaryKeyString(), command.TargetMonsterId, attributes.Get("attack"), attributes.Get("attackRange"), operationId);
        if (!attack.Accepted) return Failure(attack.ErrorCode ?? "attack_rejected", attack);
        await AwardAttackAsync(content, command.TargetMonsterId, operationId, attack);
        state.State.LastAttackAt = DateTimeOffset.UtcNow;
        CompleteOperation(operationId);
        await state.WriteStateAsync();
        return Success(attack);
    }

    public async Task<CommandResult> UseSkillAsync(UseSkillCommand command, string operationId)
    {
        if (WasProcessed(operationId)) return Success();
        var content = Content();
        if (!content.Skills.TryGetValue(command.SkillId, out var skill) || !state.State.LearnedSkillIds.Contains(skill.Id)) return Failure("unknown_skill");
        if (state.State.SkillCooldowns.TryGetValue(skill.Id, out var readyAt) && readyAt > DateTimeOffset.UtcNow) return Failure("skill_on_cooldown");
        if (skill.ResourceId is { } resource && state.State.Resources.GetValueOrDefault(resource) < skill.ResourceCost) return Failure("insufficient_resource");
        if (skill.TargetKind == SkillTargetKind.Monster && string.IsNullOrWhiteSpace(command.TargetMonsterId)) return Failure("target_required");
        var attrs = Attributes(content).Snapshot();
        var resources = new Dictionary<string, decimal>(StringComparer.Ordinal);
        if (skill.ResourceId is { } costResource) resources[costResource] = -skill.ResourceCost;
        decimal damage = 0;
        var buffs = new List<string>();
        // 兼容旧内容包的单 EffectId 与新内容包的多 EffectIds，两种形态都归一到同一结算循环。
        foreach (var effectId in skill.EffectIds.Count == 0 ? [skill.EffectId] : skill.EffectIds)
        {
            if (!content.Effects.TryGetValue(effectId, out var effect)) return Failure("unknown_effect");
            var resolved = features.GetEffect(effect.Kind).Resolve(effect, new EffectContext(attrs, command.TargetMonsterId));
            damage += resolved.MonsterDamage;
            foreach (var change in resolved.ResourceChangesOrEmpty) resources[change.Key] = resources.GetValueOrDefault(change.Key) + change.Value;
            if (resolved.BuffId is { } buff) buffs.Add(buff);
        }
        AttackResult? attack = null;
        if (damage > 0)
        {
            var range = attrs.GetValueOrDefault("attackRange", 3m);
            attack = await Zone().AttackMonsterAsync(this.GetPrimaryKeyString(), command.TargetMonsterId!, damage, range, operationId);
            if (!attack.Accepted) return Failure(attack.ErrorCode ?? "skill_rejected", attack);
            await AwardAttackAsync(content, command.TargetMonsterId!, operationId, attack);
        }
        foreach (var change in resources) state.State.Resources[change.Key] = state.State.Resources.GetValueOrDefault(change.Key) + change.Value;
        foreach (var buffId in buffs)
        {
            if (!content.Buffs.TryGetValue(buffId, out var buff)) return Failure("unknown_buff");
            state.State.ActiveBuffs[buffId] = DateTimeOffset.UtcNow.Add(buff.Duration);
        }
        state.State.SkillCooldowns[skill.Id] = DateTimeOffset.UtcNow.Add(skill.Cooldown);
        NormalizeResources(content);
        CompleteOperation(operationId);
        await state.WriteStateAsync();
        return Success(attack);
    }

    public async Task<CommandResult> EquipAsync(EquipCommand command, string operationId)
    {
        if (WasProcessed(operationId)) return Success();
        var content = Content();
        if (!content.EquipmentSlots.TryGetValue(command.SlotId, out var slot) || !content.Items.TryGetValue(command.ItemId, out var item)) return Failure("unknown_equipment");
        if (!item.Tags.Overlaps(slot.AcceptedItemTags) || state.State.Inventory.GetValueOrDefault(item.Id) < 1) return Failure("cannot_equip");
        if (state.State.EquippedItems.TryGetValue(slot.Id, out var previous)) state.State.Inventory[previous] = state.State.Inventory.GetValueOrDefault(previous) + 1;
        state.State.Inventory[item.Id]--;
        if (state.State.Inventory[item.Id] == 0) state.State.Inventory.Remove(item.Id);
        state.State.EquippedItems[slot.Id] = item.Id;
        CompleteOperation(operationId);
        await state.WriteStateAsync();
        return Success();
    }

    public async Task<CommandResult> UnequipAsync(UnequipCommand command, string operationId)
    {
        if (WasProcessed(operationId)) return Success();
        if (!state.State.EquippedItems.TryGetValue(command.SlotId, out var itemId)) return Failure("slot_empty");
        var content = Content();
        if (!InventoryRules.CanReceive(state.State.Inventory, [new RewardGrant(itemId, 1)], content.Items)) return Failure("inventory_full");
        state.State.EquippedItems.Remove(command.SlotId);
        state.State.Inventory[itemId] = state.State.Inventory.GetValueOrDefault(itemId) + 1;
        CompleteOperation(operationId);
        await state.WriteStateAsync();
        return Success();
    }

    public async Task<CommandResult> InteractNpcAsync(InteractNpcCommand command, string operationId)
    {
        if (WasProcessed(operationId)) return Success();
        var content = Content();
        if (!content.Npcs.TryGetValue(command.NpcId, out var npc) || !await Zone().IsNearNpcAsync(this.GetPrimaryKeyString(), npc.Id)) return Failure("npc_out_of_range");
        var interaction = new NpcInteraction(npc.Id, npc.QuestIds.Where(id => !state.State.Quests.ContainsKey(id)).ToArray(), npc.QuestIds.Where(id => state.State.Quests.TryGetValue(id, out var progress) && content.Quests.TryGetValue(id, out var quest) && QuestStateMachine.CanComplete(progress, quest)).ToArray());
        CompleteOperation(operationId);
        await state.WriteStateAsync();
        return Success(interaction: interaction);
    }

    public async Task<CommandResult> AcceptQuestAsync(QuestCommand command, string operationId)
    {
        if (WasProcessed(operationId)) return Success();
        var content = Content();
        if (!content.Quests.TryGetValue(command.QuestId, out var quest)) return Failure("unknown_quest");
        if (command.NpcId is { } npcId && (npcId != quest.NpcId || !await Zone().IsNearNpcAsync(this.GetPrimaryKeyString(), npcId))) return Failure("npc_out_of_range");
        state.State.Quests[quest.Id] = QuestStateMachine.Accept(state.State.Quests.GetValueOrDefault(quest.Id), quest.Id);
        CompleteOperation(operationId);
        await state.WriteStateAsync();
        return Success();
    }

    public async Task<CommandResult> CompleteQuestAsync(QuestCommand command, string operationId)
    {
        if (WasProcessed(operationId)) return Success();
        var content = Content();
        if (!content.Quests.TryGetValue(command.QuestId, out var quest) || !state.State.Quests.TryGetValue(quest.Id, out var progress) || !QuestStateMachine.CanComplete(progress, quest)) return Failure("quest_not_ready");
        if (command.NpcId is { } npcId && (npcId != quest.NpcId || !await Zone().IsNearNpcAsync(this.GetPrimaryKeyString(), npcId))) return Failure("npc_out_of_range");
        var rewards = new[] { new RewardGrant(quest.RewardItemId, quest.RewardCount) };
        if (!InventoryRules.CanReceive(state.State.Inventory, rewards, content.Items)) return Failure("inventory_full");
        if (!await rewardLedger.TryRecordAsync(this.GetPrimaryKeyString(), operationId, "quest_reward", rewards)) return Failure("duplicate_operation");
        AddRewards(rewards);
        state.State.Quests[quest.Id] = progress with { IsCompleted = true };
        CompleteOperation(operationId);
        await state.WriteStateAsync();
        return Success();
    }

    private async Task AwardAttackAsync(GameContent content, string monsterId, string operationId, AttackResult attack)
    {
        if (!attack.TargetDefeated) return;
        // 任务进度与伤害结果同属角色状态；即使该怪物没有掉落，也必须在击杀时推进进度。
        RecordQuestKill(monsterId);
        if (attack.Rewards.Count == 0) return;
        var rewards = attack.Rewards.Select(x => new RewardGrant(x.ItemId, x.Quantity)).ToArray();
        // 账本以 operationId 去重，处理 Grain 重激活或网络重试时重复发奖的风险。
        if (await rewardLedger.TryRecordAsync(this.GetPrimaryKeyString(), operationId, "monster_drop", rewards))
        {
            AddRewards(rewards);
        }
    }
    private void AddRewards(IEnumerable<RewardGrant> rewards) { foreach (var reward in rewards) state.State.Inventory[reward.ItemId] = state.State.Inventory.GetValueOrDefault(reward.ItemId) + reward.Quantity; }
    private void RecordQuestKill(string monsterId) { var content = Content(); foreach (var quest in content.Quests.Values.Where(x => x.TargetMonsterId == monsterId && state.State.Quests.ContainsKey(x.Id))) state.State.Quests[quest.Id] = QuestStateMachine.RecordKill(state.State.Quests[quest.Id], quest); }
    private IReadOnlyList<RewardGrant> PotentialDrops(GameContent content, MonsterDefinition monster) => monster.DropTableId is { } tableId && content.DropTables.TryGetValue(tableId, out var table) ? table.Entries.Select(x => new RewardGrant(x.ItemId, x.Quantity)).ToArray() : [new RewardGrant(monster.DropItemId, monster.DropCount)];
    private AttributeSet Attributes(GameContent content)
    {
        // Buff 到期在构建属性前清理，确保快照和实际战斗使用完全相同的有效 modifier 集合。
        ExpireBuffs();
        var attributes = new AttributeSet(content.Attributes, state.State.BaseAttributes);
        foreach (var itemId in state.State.EquippedItems.Values) if (content.Items.TryGetValue(itemId, out var item)) foreach (var modifier in item.Modifiers) attributes.AddModifier(modifier);
        foreach (var buffId in state.State.ActiveBuffs.Keys) if (content.Buffs.TryGetValue(buffId, out var buff)) foreach (var modifier in buff.Modifiers) attributes.AddModifier(modifier);
        return attributes;
    }
    private void NormalizeResources(GameContent content)
    {
        var attributes = Attributes(content);
        foreach (var resource in content.Resources.Values)
        {
            var maximum = attributes.Get(resource.MaximumAttributeId);
            // 首次初始化时，未明确配置初始值的资源默认满值；之后始终夹在当前最大值内，
            // 以处理装备或 Buff 变化导致最大值下降的情况。
            var current = state.State.Resources.GetValueOrDefault(resource.Id, resource.InitialValue > 0 ? resource.InitialValue : maximum);
            state.State.Resources[resource.Id] = Math.Clamp(current, 0, maximum);
        }
    }
    private void ExpireBuffs() { var now = DateTimeOffset.UtcNow; foreach (var buff in state.State.ActiveBuffs.Where(x => x.Value <= now).Select(x => x.Key).ToArray()) state.State.ActiveBuffs.Remove(buff); }
    private CharacterSnapshot Snapshot()
    {
        var content = Content(); NormalizeResources(content);
        var attributes = Attributes(content);
        var inventory = state.State.Inventory.SelectMany(x => SplitStacks(x.Key, x.Value, content.Items.GetValueOrDefault(x.Key)?.MaxStack ?? 1)).ToArray();
        return new CharacterSnapshot(this.GetPrimaryKeyString(), state.State.AccountId, state.State.Name, state.State.ZoneId, state.State.ContentVersion, state.State.Position, state.State.Level, attributes.Snapshot(), inventory, state.State.Quests.Values.ToArray());
    }
    private CharacterSnapshotV2 SnapshotV2()
    {
        var snapshot = Snapshot();
        return new CharacterSnapshotV2(snapshot, state.State.ClassId, new Dictionary<string, decimal>(state.State.Resources), state.State.EquippedItems.Select(x => new EquippedItem(x.Key, x.Value)).ToArray(), state.State.ActiveBuffs.Select(x => new ActiveBuff(x.Key, x.Value)).ToArray(), state.State.LearnedSkillIds.ToArray());
    }
    private static IEnumerable<InventoryStack> SplitStacks(string itemId, int quantity, int maxStack) { while (quantity > 0) { var amount = Math.Min(quantity, Math.Max(1, maxStack)); yield return new InventoryStack(itemId, amount); quantity -= amount; } }
    private IZoneGrain Zone() => GrainFactory.GetGrain<IZoneGrain>(state.State.ZoneId);
    private GameContent Content() => contentCatalog.GetVersion(state.State.ContentVersion);
    private static string ZoneKey(string zoneId, string contentVersion) => $"{zoneId}:{contentVersion}";
    private bool WasProcessed(string operationId) => string.IsNullOrWhiteSpace(operationId) || state.State.ProcessedOperationIds.Contains(operationId);
    // 仅保留最近的操作号，在有限状态大小与短期网络重试的幂等保障之间取平衡。
    private void CompleteOperation(string operationId) { if (!string.IsNullOrWhiteSpace(operationId)) state.State.ProcessedOperationIds.Add(operationId); if (state.State.ProcessedOperationIds.Count > 512) state.State.ProcessedOperationIds = state.State.ProcessedOperationIds.TakeLast(512).ToHashSet(StringComparer.Ordinal); }
    private CommandResult Success(AttackResult? attack = null, NpcInteraction? interaction = null) => new(true, null, Snapshot(), attack) { SnapshotV2 = SnapshotV2(), Interaction = interaction };
    private CommandResult Failure(string code, AttackResult? attack = null) => new(false, code, Snapshot(), attack) { SnapshotV2 = SnapshotV2() };
}
