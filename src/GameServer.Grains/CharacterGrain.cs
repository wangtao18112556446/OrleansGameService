using GameServer.Contracts;
using GameServer.Domain.Attributes;
using GameServer.Domain.Gameplay;
using GameServer.Grains.State;
using Orleans.Runtime;

namespace GameServer.Grains;

/// <summary>串行处理角色命令，以角色状态为库存权威，并恢复跨 Grain 战斗和奖励账本投递。</summary>
public sealed class CharacterGrain(
    [PersistentState("character", "gameStore")] IPersistentState<CharacterState> state,
    IGameContentCatalog contentCatalog,
    IRewardLedger rewardLedger,
    GameFeatureRegistry features) : Grain, ICharacterGrain
{
    public async Task InitializeAsync(string accountId, string name, string contentVersion, string classId)
    {
        await RecoverAsync();
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
        await SaveAsync();
    }

    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        await RecoverAsync();
        await base.OnActivateAsync(cancellationToken);
    }

    public async Task<CharacterSnapshot> GetSnapshotAsync() { await RecoverAsync(); return Snapshot(); }
    public async Task<CharacterSnapshotV2> GetSnapshotV2Async() { await RecoverAsync(); return SnapshotV2(); }

    public async Task<CommandResult> EnterZoneAsync(string zoneId, string contentVersion)
    {
        var content = contentCatalog.GetVersion(contentVersion);
        if (!content.Zones.ContainsKey(zoneId)) return Failure("invalid_zone");
        await RecoverAsync();
        state.State.PendingZoneTransfer = new PendingZoneTransfer
        {
            PreviousZoneId = state.State.ZoneId, TargetZoneId = ZoneKey(zoneId, contentVersion), ContentVersion = contentVersion
        };
        await SaveAsync();
        if (!await ResumeZoneTransferAsync()) return Failure("zone_unavailable");
        return Success();
    }

    public async Task<CommandResult> MoveAsync(MoveCommand command, string operationId)
    {
        if (!float.IsFinite(command.X) || !float.IsFinite(command.Y)) return Failure("invalid_movement");
        var fingerprint = OperationIdentity.Fingerprint("move", command);
        if (await ReplayAsync(operationId, fingerprint) is { } replay) return replay;
        var next = new WorldPosition(command.X, command.Y);
        if (!CombatRules.ValidateMove(state.State.Position, next, 12f).Allowed) return Failure("invalid_movement");
        state.State.PendingMove = new PendingMove { OperationId = operationId, Fingerprint = fingerprint, Position = next };
        await SaveAsync();
        await ResumeMoveAsync();
        return ReceiptResult(state.State.OperationReceipts[operationId]);
    }

    public async Task<CommandResult> AttackAsync(AttackCommand command, string operationId)
    {
        var fingerprint = OperationIdentity.Fingerprint("attack", command);
        if (await ReplayAsync(operationId, fingerprint) is { } replay) return replay;
        if (state.State.LastAttackAt is { } last && DateTimeOffset.UtcNow - last < TimeSpan.FromMilliseconds(500)) return Failure("attack_on_cooldown");
        var content = Content();
        if (!content.Monsters.TryGetValue(command.TargetMonsterId, out var monster)) return Failure("unknown_target");
        // 先按最坏情况下的掉落预检背包。这样击杀后不会出现奖励已生成却无处存放的半完成状态。
        var potential = PotentialDrops(content, monster);
        if (!InventoryRules.CanReceive(state.State.Inventory, potential, content.Items)) return Failure("inventory_full");
        var attributes = Attributes(content);
        state.State.PendingCombat = new PendingCombat
        {
            OperationId = operationId, Fingerprint = fingerprint, MonsterId = command.TargetMonsterId,
            Damage = attributes.Get("attack"), Range = attributes.Get("attackRange"), ExecutedAt = DateTimeOffset.UtcNow
        };
        await SaveAsync();
        await ResumeCombatAsync();
        await FlushRewardsAsync();
        return ReceiptResult(state.State.OperationReceipts[operationId]);
    }

    public async Task<CommandResult> UseSkillAsync(UseSkillCommand command, string operationId)
    {
        var fingerprint = OperationIdentity.Fingerprint("skill", command);
        if (await ReplayAsync(operationId, fingerprint) is { } replay) return replay;
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
        foreach (var buffId in buffs) if (!content.Buffs.ContainsKey(buffId)) return Failure("unknown_buff");
        if (damage > 0)
        {
            if (command.TargetMonsterId is null || !content.Monsters.TryGetValue(command.TargetMonsterId, out var monster)) return Failure("unknown_target");
            if (!InventoryRules.CanReceive(state.State.Inventory, PotentialDrops(content, monster), content.Items)) return Failure("inventory_full");
            state.State.PendingCombat = new PendingCombat
            {
                OperationId = operationId, Fingerprint = fingerprint, MonsterId = command.TargetMonsterId,
                Damage = damage, Range = attrs.GetValueOrDefault("attackRange", 3m), SkillId = skill.Id,
                ExecutedAt = DateTimeOffset.UtcNow, ResourceChanges = resources, BuffIds = buffs
            };
            await SaveAsync();
            await ResumeCombatAsync();
            await FlushRewardsAsync();
            return ReceiptResult(state.State.OperationReceipts[operationId]);
        }
        foreach (var change in resources) state.State.Resources[change.Key] = state.State.Resources.GetValueOrDefault(change.Key) + change.Value;
        foreach (var buffId in buffs)
        {
            if (!content.Buffs.TryGetValue(buffId, out var buff)) return Failure("unknown_buff");
            state.State.ActiveBuffs[buffId] = DateTimeOffset.UtcNow.Add(buff.Duration);
        }
        state.State.SkillCooldowns[skill.Id] = DateTimeOffset.UtcNow.Add(skill.Cooldown);
        NormalizeResources(content);
        CompleteOperation(operationId, fingerprint);
        await SaveAsync();
        return Success();
    }

    public async Task<CommandResult> EquipAsync(EquipCommand command, string operationId)
    {
        var fingerprint = OperationIdentity.Fingerprint("equip", command);
        if (await ReplayAsync(operationId, fingerprint) is { } replay) return replay;
        var content = Content();
        if (!content.EquipmentSlots.TryGetValue(command.SlotId, out var slot) || !content.Items.TryGetValue(command.ItemId, out var item)) return Failure("unknown_equipment");
        if (!item.Tags.Overlaps(slot.AcceptedItemTags) || state.State.Inventory.GetValueOrDefault(item.Id) < 1) return Failure("cannot_equip");
        if (state.State.EquippedItems.TryGetValue(slot.Id, out var previous)) state.State.Inventory[previous] = state.State.Inventory.GetValueOrDefault(previous) + 1;
        state.State.Inventory[item.Id]--;
        if (state.State.Inventory[item.Id] == 0) state.State.Inventory.Remove(item.Id);
        state.State.EquippedItems[slot.Id] = item.Id;
        CompleteOperation(operationId, fingerprint);
        await SaveAsync();
        return Success();
    }

    public async Task<CommandResult> UnequipAsync(UnequipCommand command, string operationId)
    {
        var fingerprint = OperationIdentity.Fingerprint("unequip", command);
        if (await ReplayAsync(operationId, fingerprint) is { } replay) return replay;
        if (!state.State.EquippedItems.TryGetValue(command.SlotId, out var itemId)) return Failure("slot_empty");
        var content = Content();
        if (!InventoryRules.CanReceive(state.State.Inventory, [new RewardGrant(itemId, 1)], content.Items)) return Failure("inventory_full");
        state.State.EquippedItems.Remove(command.SlotId);
        state.State.Inventory[itemId] = state.State.Inventory.GetValueOrDefault(itemId) + 1;
        CompleteOperation(operationId, fingerprint);
        await SaveAsync();
        return Success();
    }

    public async Task<CommandResult> InteractNpcAsync(InteractNpcCommand command, string operationId)
    {
        var fingerprint = OperationIdentity.Fingerprint("npc", command);
        if (await ReplayAsync(operationId, fingerprint) is { } replay) return replay;
        var content = Content();
        if (!content.Npcs.TryGetValue(command.NpcId, out var npc) || !await Zone().IsNearNpcAsync(this.GetPrimaryKeyString(), npc.Id)) return Failure("npc_out_of_range");
        var interaction = new NpcInteraction(npc.Id, npc.QuestIds.Where(id => !state.State.Quests.ContainsKey(id)).ToArray(), npc.QuestIds.Where(id => state.State.Quests.TryGetValue(id, out var progress) && content.Quests.TryGetValue(id, out var quest) && QuestStateMachine.CanComplete(progress, quest)).ToArray());
        CompleteOperation(operationId, fingerprint, interaction: interaction);
        await SaveAsync();
        return Success(interaction: interaction);
    }

    public async Task<CommandResult> AcceptQuestAsync(QuestCommand command, string operationId)
    {
        var fingerprint = OperationIdentity.Fingerprint("accept_quest", command);
        if (await ReplayAsync(operationId, fingerprint) is { } replay) return replay;
        var content = Content();
        if (!content.Quests.TryGetValue(command.QuestId, out var quest)) return Failure("unknown_quest");
        if (command.NpcId is { } npcId && (npcId != quest.NpcId || !await Zone().IsNearNpcAsync(this.GetPrimaryKeyString(), npcId))) return Failure("npc_out_of_range");
        state.State.Quests[quest.Id] = QuestStateMachine.Accept(state.State.Quests.GetValueOrDefault(quest.Id), quest.Id);
        CompleteOperation(operationId, fingerprint);
        await SaveAsync();
        return Success();
    }

    public async Task<CommandResult> CompleteQuestAsync(QuestCommand command, string operationId)
    {
        var fingerprint = OperationIdentity.Fingerprint("complete_quest", command);
        if (await ReplayAsync(operationId, fingerprint) is { } replay) return replay;
        var content = Content();
        if (!content.Quests.TryGetValue(command.QuestId, out var quest) || !state.State.Quests.TryGetValue(quest.Id, out var progress) || !QuestStateMachine.CanComplete(progress, quest)) return Failure("quest_not_ready");
        if (command.NpcId is { } npcId && (npcId != quest.NpcId || !await Zone().IsNearNpcAsync(this.GetPrimaryKeyString(), npcId))) return Failure("npc_out_of_range");
        var rewards = new[] { new RewardGrant(quest.RewardItemId, quest.RewardCount) };
        if (!InventoryRules.CanReceive(state.State.Inventory, rewards, content.Items)) return Failure("inventory_full");
        QueueRewards(operationId, "quest_reward", rewards);
        state.State.Quests[quest.Id] = progress with { IsCompleted = true };
        CompleteOperation(operationId, fingerprint);
        await SaveAsync();
        await FlushRewardsAsync();
        return Success();
    }

    private void AwardAttack(string monsterId, string operationId, AttackResult attack)
    {
        if (!attack.TargetDefeated) return;
        // 任务进度与伤害结果同属角色状态；即使该怪物没有掉落，也必须在击杀时推进进度。
        RecordQuestKill(monsterId);
        if (attack.Rewards.Count == 0) return;
        var rewards = attack.Rewards.Select(x => new RewardGrant(x.ItemId, x.Quantity)).ToArray();
        QueueRewards(operationId, "monster_drop", rewards);
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
    private bool reloadRequired;

    private async Task SaveAsync()
    {
        try { await state.WriteStateAsync(); }
        catch
        {
            // 写入超时可能已经提交；下次请求必须重新读取，不能继续使用不确定的内存状态。
            reloadRequired = true;
            throw;
        }
    }

    private async Task RecoverAsync()
    {
        if (reloadRequired) { await state.ReadStateAsync(); reloadRequired = false; }
        if (state.State.PendingZoneTransfer is not null) await ResumeZoneTransferAsync();
        if (state.State.PendingMove is not null) await ResumeMoveAsync();
        if (state.State.PendingCombat is not null) await ResumeCombatAsync();
        await FlushRewardsAsync();
    }

    private async Task<CommandResult?> ReplayAsync(string operationId, string fingerprint)
    {
        await RecoverAsync();
        if (!OperationIdentity.IsValid(operationId)) return Failure("invalid_operation_id");
        if (state.State.OperationReceipts.TryGetValue(operationId, out var receipt))
            return receipt.Fingerprint == fingerprint ? ReceiptResult(receipt) : Failure("operation_conflict");
        // 旧状态只存操作号，无法证明请求相同；拒绝重用，避免升级后重复执行。
        if (state.State.ProcessedOperationIds.Contains(operationId)) return Failure("operation_expired");
        return null;
    }

    private CommandResult ReceiptResult(OperationReceipt receipt) => receipt.ErrorCode is { } error
        ? Failure(error, receipt.Attack) : Success(receipt.Attack, receipt.Interaction);

    private void CompleteOperation(string operationId, string fingerprint, AttackResult? attack = null, NpcInteraction? interaction = null, string? error = null)
    {
        // 不再任意淘汰已执行操作：超出缓存窗口后重放击杀仍可能重复发奖。
        // 后续规模化时需先引入带服务端期限的操作协议，再安全归档这些紧凑回执。
        state.State.OperationReceipts[operationId] = new OperationReceipt
        {
            Fingerprint = fingerprint, Attack = attack, Interaction = interaction, ErrorCode = error
        };
    }

    private void QueueRewards(string operationId, string kind, IReadOnlyList<RewardGrant> rewards)
    {
        AddRewards(rewards);
        state.State.PendingRewards.Add(new PendingReward { OperationId = operationId, Kind = kind, Rewards = rewards.ToList() });
    }

    private async Task FlushRewardsAsync()
    {
        while (state.State.PendingRewards.FirstOrDefault() is { } pending)
        {
            // false 只代表完全相同的投递已经存在；冲突和数据库故障必须抛出，保留发件箱重试。
            await rewardLedger.TryRecordAsync(this.GetPrimaryKeyString(), pending.OperationId, pending.Kind, pending.Rewards);
            state.State.PendingRewards.RemoveAt(0);
            await SaveAsync();
        }
    }

    private async Task ResumeMoveAsync()
    {
        var pending = state.State.PendingMove!;
        var moved = await Zone().MoveAsync(this.GetPrimaryKeyString(), pending.Position);
        if (moved) state.State.Position = pending.Position;
        CompleteOperation(pending.OperationId, pending.Fingerprint, error: moved ? null : "not_in_zone");
        state.State.PendingMove = null;
        await SaveAsync();
    }

    private async Task ResumeCombatAsync()
    {
        var pending = state.State.PendingCombat!;
        var attack = await Zone().AttackMonsterAsync(this.GetPrimaryKeyString(), pending.MonsterId, pending.Damage, pending.Range, pending.OperationId);
        if (attack.Accepted)
        {
            AwardAttack(pending.MonsterId, pending.OperationId, attack);
            if (pending.SkillId is { } skillId)
            {
                var content = Content();
                foreach (var change in pending.ResourceChanges) state.State.Resources[change.Key] = state.State.Resources.GetValueOrDefault(change.Key) + change.Value;
                foreach (var buffId in pending.BuffIds) state.State.ActiveBuffs[buffId] = pending.ExecutedAt.Add(content.Buffs[buffId].Duration);
                state.State.SkillCooldowns[skillId] = pending.ExecutedAt.Add(content.Skills[skillId].Cooldown);
                NormalizeResources(content);
            }
            else state.State.LastAttackAt = pending.ExecutedAt;
        }
        CompleteOperation(pending.OperationId, pending.Fingerprint, attack, error: attack.Accepted ? null : attack.ErrorCode ?? "attack_rejected");
        state.State.PendingCombat = null;
        // 奖励、任务进度、技能消耗、回执和发件箱一次提交，恢复时不会部分重复结算。
        await SaveAsync();
    }

    private async Task<bool> ResumeZoneTransferAsync()
    {
        var pending = state.State.PendingZoneTransfer!;
        if (!pending.Joined)
        {
            try { await GrainFactory.GetGrain<IZoneGrain>(pending.TargetZoneId).JoinAsync(this.GetPrimaryKeyString(), new WorldPosition(0, 0), pending.ContentVersion); }
            catch (InvalidOperationException)
            {
                state.State.PendingZoneTransfer = null;
                await SaveAsync();
                return false;
            }
            state.State.ZoneId = pending.TargetZoneId;
            state.State.ContentVersion = pending.ContentVersion;
            state.State.Position = new WorldPosition(0, 0);
            NormalizeResources(Content());
            pending.Joined = true;
            await SaveAsync();
        }
        if (pending.PreviousZoneId != pending.TargetZoneId && !string.IsNullOrWhiteSpace(pending.PreviousZoneId))
            await GrainFactory.GetGrain<IZoneGrain>(pending.PreviousZoneId).LeaveAsync(this.GetPrimaryKeyString());
        state.State.PendingZoneTransfer = null;
        await SaveAsync();
        return true;
    }
    private CommandResult Success(AttackResult? attack = null, NpcInteraction? interaction = null) => new(true, null, Snapshot(), attack) { SnapshotV2 = SnapshotV2(), Interaction = interaction };
    private CommandResult Failure(string code, AttackResult? attack = null) => new(false, code, Snapshot(), attack) { SnapshotV2 = SnapshotV2() };
}
