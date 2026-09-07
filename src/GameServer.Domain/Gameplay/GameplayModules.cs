using GameServer.Abstractions;
using GameServer.Contracts;

namespace GameServer.Domain.Gameplay;

/// <summary>从所有显式注册的效果构建只读目录，并在重复类型标识时拒绝启动。</summary>
public sealed class GameplayFeatureCatalog : IGameplayFeatureCatalog
{
    private readonly IReadOnlyDictionary<string, IGameEffect> effects;

    public GameplayFeatureCatalog(IEnumerable<IGameEffect> effects)
    {
        var registered = new Dictionary<string, IGameEffect>(StringComparer.Ordinal);
        foreach (var effect in effects)
        {
            if (string.IsNullOrWhiteSpace(effect.TypeId)) throw new InvalidOperationException($"Effect '{effect.GetType().Name}' has an empty type id.");
            if (!registered.TryAdd(effect.TypeId, effect))
                throw new InvalidOperationException($"Effect type '{effect.TypeId}' is registered by both '{registered[effect.TypeId].GetType().Name}' and '{effect.GetType().Name}'.");
        }
        this.effects = registered;
    }

    public IGameEffect GetEffect(string typeId)
        => effects.TryGetValue(typeId, out var effect)
            ? effect
            : throw new InvalidOperationException($"Effect type '{typeId}' is not registered.");

    public bool Supports(string typeId) => effects.ContainsKey(typeId);
}

/// <summary>解析基于属性缩放的数值，并统一阻止效果产生负数绝对值。</summary>
public static class EffectValue
{
    public static decimal Resolve(EffectDefinition effect, IReadOnlyDictionary<string, decimal> attributes)
        => Math.Max(0m, effect.Amount + (effect.ScalingAttributeId is { } id ? attributes.GetValueOrDefault(id) * effect.ScalingFactor : 0m));
}

/// <summary>把配置效果解析为对怪物造成的非负伤害。</summary>
public sealed class DamageMonsterEffect : IGameEffect
{
    public string TypeId => BuiltInEffectTypes.DamageMonster;
    public IEnumerable<ContentValidationIssue> Validate(EffectDefinition effect, GameContentVersion content) => [];
    public EffectResolution Resolve(EffectDefinition effect, EffectContext context)
        => new(MonsterDamage: EffectValue.Resolve(effect, context.Attributes));
}

/// <summary>把配置效果解析为指定角色资源的恢复量。</summary>
public sealed class RestoreResourceEffect : IGameEffect
{
    public string TypeId => BuiltInEffectTypes.RestoreResource;

    public IEnumerable<ContentValidationIssue> Validate(EffectDefinition effect, GameContentVersion content)
        => ValidateResource(effect, content, TypeId);

    public EffectResolution Resolve(EffectDefinition effect, EffectContext context)
        => new(ResourceChanges: new Dictionary<string, decimal>
        {
            [effect.ResourceId ?? throw new InvalidOperationException("Resource effect requires a resource id.")] = EffectValue.Resolve(effect, context.Attributes)
        });

    internal static IEnumerable<ContentValidationIssue> ValidateResource(EffectDefinition effect, GameContentVersion content, string typeId)
    {
        if (string.IsNullOrWhiteSpace(effect.ResourceId))
            yield return new($"$.effects.{effect.Id}.resourceId", "resource_required", $"Effect type '{typeId}' requires a resource id.");
        else if (!content.Content.Resources.ContainsKey(effect.ResourceId))
            yield return new($"$.effects.{effect.Id}.resourceId", "unknown_resource", $"Effect '{effect.Id}' references unknown resource '{effect.ResourceId}'.");
    }
}

/// <summary>把配置效果解析为指定角色资源的消耗量。</summary>
public sealed class ConsumeResourceEffect : IGameEffect
{
    public string TypeId => BuiltInEffectTypes.ConsumeResource;

    public IEnumerable<ContentValidationIssue> Validate(EffectDefinition effect, GameContentVersion content)
        => RestoreResourceEffect.ValidateResource(effect, content, TypeId);

    public EffectResolution Resolve(EffectDefinition effect, EffectContext context)
        => new(ResourceChanges: new Dictionary<string, decimal>
        {
            [effect.ResourceId ?? throw new InvalidOperationException("Resource effect requires a resource id.")] = -EffectValue.Resolve(effect, context.Attributes)
        });
}

/// <summary>把配置效果解析为对角色施加的 Buff，并验证目标 Buff 存在。</summary>
public sealed class ApplyBuffEffect : IGameEffect
{
    public string TypeId => BuiltInEffectTypes.ApplyBuff;

    public IEnumerable<ContentValidationIssue> Validate(EffectDefinition effect, GameContentVersion content)
    {
        if (string.IsNullOrWhiteSpace(effect.BuffId))
            yield return new($"$.effects.{effect.Id}.buffId", "buff_required", "Apply-buff effect requires a buff id.");
        else if (!content.Content.Buffs.ContainsKey(effect.BuffId))
            yield return new($"$.effects.{effect.Id}.buffId", "unknown_buff", $"Effect '{effect.Id}' references unknown buff '{effect.BuffId}'.");
    }

    public EffectResolution Resolve(EffectDefinition effect, EffectContext context)
        => new(BuffId: effect.BuffId ?? throw new InvalidOperationException("Apply-buff effect requires a buff id."));
}

/// <summary>校验核心 RPG 内容图及效果实现引用，并聚合全部可定位问题。</summary>
public sealed class CoreGameContentValidator(IGameplayFeatureCatalog features) : IGameContentValidator
{
    public IEnumerable<ContentValidationIssue> Validate(GameContentVersion version)
    {
        var content = version.Content;
        if (string.IsNullOrWhiteSpace(content.Version)) yield return Issue("$.version", "version_required", "Content version is required.");
        ContentValidationIssue? movementIssue = null;
        try { _ = MovementRules.Capacity(content.Movement); }
        catch (Exception exception) { movementIssue = Issue("$.movement", "invalid_movement", exception.Message); }
        if (movementIssue is not null) yield return movementIssue;

        foreach (var item in content.Items.Values)
            if (item.MaxStack <= 0) yield return Issue($"$.items.{item.Id}.maxStack", "invalid_max_stack", $"Item '{item.Id}' must have a positive max stack.");

        foreach (var characterClass in content.Classes.Values)
        {
            foreach (var attribute in characterClass.InitialAttributes.Keys)
                if (!content.Attributes.ContainsKey(attribute)) yield return Issue($"$.classes.{characterClass.Id}.initialAttributes.{attribute}", "unknown_attribute", $"Class '{characterClass.Id}' references unknown attribute '{attribute}'.");
            foreach (var skill in characterClass.InitialSkillIds)
                if (!content.Skills.ContainsKey(skill)) yield return Issue($"$.classes.{characterClass.Id}.initialSkillIds", "unknown_skill", $"Class '{characterClass.Id}' references unknown skill '{skill}'.");
            foreach (var item in characterClass.InitialItems)
                if (!content.Items.ContainsKey(item.ItemId) || item.Quantity <= 0) yield return Issue($"$.classes.{characterClass.Id}.initialItems", "invalid_initial_item", $"Class '{characterClass.Id}' has invalid initial item '{item.ItemId}'.");
        }

        foreach (var resource in content.Resources.Values)
            if (!content.Attributes.ContainsKey(resource.MaximumAttributeId)) yield return Issue($"$.resources.{resource.Id}.maximumAttributeId", "unknown_attribute", $"Resource '{resource.Id}' references unknown maximum attribute.");
        foreach (var slot in content.EquipmentSlots.Values)
            if (slot.AcceptedItemTags.Count == 0) yield return Issue($"$.equipmentSlots.{slot.Id}.acceptedItemTags", "empty_item_tags", $"Equipment slot '{slot.Id}' must accept a tag.");
        foreach (var item in content.Items.Values)
            foreach (var modifier in item.Modifiers)
                if (!content.Attributes.ContainsKey(modifier.AttributeId)) yield return Issue($"$.items.{item.Id}.modifiers", "unknown_attribute", $"Item '{item.Id}' references unknown attribute '{modifier.AttributeId}'.");
        foreach (var buff in content.Buffs.Values)
            foreach (var modifier in buff.Modifiers)
                if (!content.Attributes.ContainsKey(modifier.AttributeId)) yield return Issue($"$.buffs.{buff.Id}.modifiers", "unknown_attribute", $"Buff '{buff.Id}' references unknown attribute '{modifier.AttributeId}'.");

        foreach (var skill in content.Skills.Values)
        {
            if (skill.ResourceId is { } resourceId && !content.Resources.ContainsKey(resourceId)) yield return Issue($"$.skills.{skill.Id}.resourceId", "unknown_resource", $"Skill '{skill.Id}' references unknown resource '{resourceId}'.");
            foreach (var effectId in skill.EffectIds.Count == 0 ? [skill.EffectId] : skill.EffectIds)
                if (!content.Effects.ContainsKey(effectId)) yield return Issue($"$.skills.{skill.Id}.effectIds", "unknown_effect", $"Skill '{skill.Id}' references unknown effect '{effectId}'.");
        }

        foreach (var effect in content.Effects.Values)
        {
            string? typeId = null;
            ContentValidationIssue? typeIssue = null;
            try { typeId = BuiltInEffectTypes.Resolve(effect); }
            catch (Exception exception) { typeIssue = Issue($"$.effects.{effect.Id}.typeId", "invalid_effect_type", exception.Message); }
            if (typeIssue is not null) { yield return typeIssue; continue; }
            var resolvedTypeId = typeId!;
            if (!features.Supports(resolvedTypeId))
            {
                yield return Issue($"$.effects.{effect.Id}.typeId", "unsupported_effect", $"Effect '{effect.Id}' uses unregistered type '{resolvedTypeId}'.");
                continue;
            }
            if (effect.ScalingAttributeId is { } attributeId && !content.Attributes.ContainsKey(attributeId))
                yield return Issue($"$.effects.{effect.Id}.scalingAttributeId", "unknown_attribute", $"Effect '{effect.Id}' references unknown scaling attribute '{attributeId}'.");
            foreach (var issue in features.GetEffect(resolvedTypeId).Validate(effect, version)) yield return issue;
        }

        foreach (var table in content.DropTables.Values)
            foreach (var entry in table.Entries)
                if (!content.Items.ContainsKey(entry.ItemId) || entry.Quantity <= 0 || entry.Probability is < 0m or > 1m)
                    yield return Issue($"$.dropTables.{table.Id}.entries", "invalid_drop_entry", $"Drop table '{table.Id}' has an invalid entry for '{entry.ItemId}'.");
        foreach (var monster in content.Monsters.Values)
        {
            if (monster.DropTableId is { } tableId && !content.DropTables.ContainsKey(tableId)) yield return Issue($"$.monsters.{monster.Id}.dropTableId", "unknown_drop_table", $"Monster '{monster.Id}' references unknown drop table '{tableId}'.");
            else if (monster.DropTableId is null && (!content.Items.ContainsKey(monster.DropItemId) || monster.DropCount <= 0)) yield return Issue($"$.monsters.{monster.Id}", "invalid_monster_drop", $"Monster '{monster.Id}' references an invalid item drop.");
        }
        foreach (var npc in content.Npcs.Values)
        {
            if (!content.Zones.ContainsKey(npc.ZoneId)) yield return Issue($"$.npcs.{npc.Id}.zoneId", "unknown_zone", $"NPC '{npc.Id}' references unknown zone '{npc.ZoneId}'.");
            foreach (var questId in npc.QuestIds)
                if (!content.Quests.ContainsKey(questId)) yield return Issue($"$.npcs.{npc.Id}.questIds", "unknown_quest", $"NPC '{npc.Id}' references unknown quest '{questId}'.");
        }
        foreach (var quest in content.Quests.Values)
        {
            if (!content.Npcs.ContainsKey(quest.NpcId)) yield return Issue($"$.quests.{quest.Id}.npcId", "unknown_npc", $"Quest '{quest.Id}' references unknown NPC '{quest.NpcId}'.");
            if (!content.Monsters.ContainsKey(quest.TargetMonsterId)) yield return Issue($"$.quests.{quest.Id}.targetMonsterId", "unknown_monster", $"Quest '{quest.Id}' references unknown monster '{quest.TargetMonsterId}'.");
            if (!content.Items.ContainsKey(quest.RewardItemId)) yield return Issue($"$.quests.{quest.Id}.rewardItemId", "unknown_item", $"Quest '{quest.Id}' references unknown item '{quest.RewardItemId}'.");
        }
    }

    private static ContentValidationIssue Issue(string path, string code, string message) => new(path, code, message, "core.gameplay");
}

/// <summary>注册框架内置 RPG 效果与核心内容校验。</summary>
public static class CoreGameplayModuleExtensions
{
    public static GameServerBuilder AddCoreGameplay(this GameServerBuilder builder)
        => builder.AddModule(new GameModuleDescriptor("core.gameplay"), module => module
            .AddEffect<DamageMonsterEffect>()
            .AddEffect<RestoreResourceEffect>()
            .AddEffect<ConsumeResourceEffect>()
            .AddEffect<ApplyBuffEffect>()
            .AddContentValidator<CoreGameContentValidator>());
}
