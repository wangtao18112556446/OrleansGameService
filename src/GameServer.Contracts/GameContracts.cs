using MessagePack;

namespace GameServer.Contracts;

[MessagePackObject]
[Orleans.GenerateSerializer(GenerateFieldIds = Orleans.GenerateFieldIds.PublicProperties)]
[Alias("GameServer.Contracts.WorldPosition")]
public readonly record struct WorldPosition([property: Key(0)] float X, [property: Key(1)] float Y)
{
    public float DistanceTo(WorldPosition other) => MathF.Sqrt(MathF.Pow(X - other.X, 2) + MathF.Pow(Y - other.Y, 2));
}

[Orleans.GenerateSerializer(GenerateFieldIds = Orleans.GenerateFieldIds.PublicProperties)]
[Alias("GameServer.Contracts.AttributeDefinition")]
public sealed record AttributeDefinition(string Id, decimal DefaultValue, decimal? Minimum = null, decimal? Maximum = null);

[Orleans.GenerateSerializer(GenerateFieldIds = Orleans.GenerateFieldIds.PublicProperties)]
[Alias("GameServer.Contracts.AttributeModifier")]
public sealed record AttributeModifier(string AttributeId, decimal FlatAmount, decimal PercentAmount, string Source);

[Orleans.GenerateSerializer(GenerateFieldIds = Orleans.GenerateFieldIds.PublicProperties)]
[Alias("GameServer.Contracts.StartingItem")]
public sealed record StartingItem(string ItemId, int Quantity);

[Orleans.GenerateSerializer(GenerateFieldIds = Orleans.GenerateFieldIds.PublicProperties)]
[Alias("GameServer.Contracts.CharacterClassDefinition")]
public sealed record CharacterClassDefinition(string Id, string DisplayName, IReadOnlyDictionary<string, decimal> InitialAttributes)
{
    public IReadOnlyList<string> InitialSkillIds { get; init; } = [];
    public IReadOnlyList<StartingItem> InitialItems { get; init; } = [];
}

[Orleans.GenerateSerializer(GenerateFieldIds = Orleans.GenerateFieldIds.PublicProperties)]
[Alias("GameServer.Contracts.ResourceDefinition")]
public sealed record ResourceDefinition(string Id, string DisplayName, string MaximumAttributeId) { public decimal InitialValue { get; init; } }

[Orleans.GenerateSerializer(GenerateFieldIds = Orleans.GenerateFieldIds.PublicProperties)]
[Alias("GameServer.Contracts.ItemDefinition")]
public sealed record ItemDefinition(string Id, string DisplayName, int MaxStack = 99)
{
    public IReadOnlySet<string> Tags { get; init; } = new HashSet<string>(StringComparer.Ordinal);
    public IReadOnlyList<AttributeModifier> Modifiers { get; init; } = [];
}

[Orleans.GenerateSerializer(GenerateFieldIds = Orleans.GenerateFieldIds.PublicProperties)]
[Alias("GameServer.Contracts.EquipmentSlotDefinition")]
public sealed record EquipmentSlotDefinition(string Id, string DisplayName, IReadOnlySet<string> AcceptedItemTags);
public enum SkillTargetKind { Self, Monster }

[Orleans.GenerateSerializer(GenerateFieldIds = Orleans.GenerateFieldIds.PublicProperties)]
[Alias("GameServer.Contracts.SkillDefinition")]
public sealed record SkillDefinition(string Id, string DisplayName, decimal ResourceCost, TimeSpan Cooldown, string EffectId)
{
    public string? ResourceId { get; init; }
    public SkillTargetKind TargetKind { get; init; } = SkillTargetKind.Monster;
    public IReadOnlyList<string> EffectIds { get; init; } = [];
}

[Orleans.GenerateSerializer(GenerateFieldIds = Orleans.GenerateFieldIds.PublicProperties)]
[Alias("GameServer.Contracts.BuffDefinition")]
public sealed record BuffDefinition(string Id, string DisplayName, TimeSpan Duration, IReadOnlyList<AttributeModifier> Modifiers);

[Orleans.GenerateSerializer(GenerateFieldIds = Orleans.GenerateFieldIds.PublicProperties)]
[Alias("GameServer.Contracts.NpcDefinition")]
public sealed record NpcDefinition(string Id, string DisplayName, WorldPosition Position, IReadOnlyList<string> QuestIds)
{
    public string ZoneId { get; init; } = "starter-plains";
    public decimal InteractionRange { get; init; } = 3m;
}

[Orleans.GenerateSerializer(GenerateFieldIds = Orleans.GenerateFieldIds.PublicProperties)]
[Alias("GameServer.Contracts.ZoneDefinition")]
public sealed record ZoneDefinition(string Id, string DisplayName, int Capacity);

[Orleans.GenerateSerializer(GenerateFieldIds = Orleans.GenerateFieldIds.PublicProperties)]
[Alias("GameServer.Contracts.DropEntry")]
public sealed record DropEntry(string ItemId, int Quantity, decimal Probability);
[Orleans.GenerateSerializer(GenerateFieldIds = Orleans.GenerateFieldIds.PublicProperties)]
[Alias("GameServer.Contracts.DropTableDefinition")]
public sealed record DropTableDefinition(string Id, IReadOnlyList<DropEntry> Entries);

[Orleans.GenerateSerializer(GenerateFieldIds = Orleans.GenerateFieldIds.PublicProperties)]
[Alias("GameServer.Contracts.MonsterDefinition")]
public sealed record MonsterDefinition(string Id, string DisplayName, decimal Health, decimal Attack, WorldPosition SpawnPosition, string DropItemId, int DropCount) { public string? DropTableId { get; init; } }
[Orleans.GenerateSerializer(GenerateFieldIds = Orleans.GenerateFieldIds.PublicProperties)]
[Alias("GameServer.Contracts.QuestDefinition")]
public sealed record QuestDefinition(string Id, string NpcId, string TargetMonsterId, int RequiredKills, string RewardItemId, int RewardCount);
public enum EffectKind { DamageMonster, RestoreResource, ConsumeResource, ApplyBuff }

/// <summary>定义内容中的效果参数，并通过可选 TypeId 接入编译期扩展模块。</summary>
[Orleans.GenerateSerializer(GenerateFieldIds = Orleans.GenerateFieldIds.PublicProperties)]
[Alias("GameServer.Contracts.EffectDefinition")]
public sealed record EffectDefinition(string Id, EffectKind Kind, decimal Amount = 0m)
{
    public string? ResourceId { get; init; }
    public string? BuffId { get; init; }
    public string? ScalingAttributeId { get; init; }
    public decimal ScalingFactor { get; init; }
    /// <summary>指定可扩展效果实现；为空时按兼容的 Kind 映射内置实现。</summary>
    public string? TypeId { get; init; }
}

/// <summary>定义角色移动速度以及服务器最多允许累积的移动时间预算。</summary>
[Orleans.GenerateSerializer(GenerateFieldIds = Orleans.GenerateFieldIds.PublicProperties)]
[Alias("GameServer.Contracts.MovementDefinition")]
public sealed record MovementDefinition(float SpeedPerSecond, TimeSpan MaximumBudget);

/// <summary>聚合一个不可变内容版本中的玩法定义，并为旧内容提供兼容缺省值。</summary>
[Orleans.GenerateSerializer(GenerateFieldIds = Orleans.GenerateFieldIds.PublicProperties)]
[Alias("GameServer.Contracts.GameContent")]
public sealed record GameContent(string Version, IReadOnlyDictionary<string, AttributeDefinition> Attributes, IReadOnlyDictionary<string, ItemDefinition> Items, IReadOnlyDictionary<string, MonsterDefinition> Monsters, IReadOnlyDictionary<string, QuestDefinition> Quests)
{
    public IReadOnlyDictionary<string, CharacterClassDefinition> Classes { get; init; } = new Dictionary<string, CharacterClassDefinition>();
    public IReadOnlyDictionary<string, ResourceDefinition> Resources { get; init; } = new Dictionary<string, ResourceDefinition>();
    public IReadOnlyDictionary<string, EquipmentSlotDefinition> EquipmentSlots { get; init; } = new Dictionary<string, EquipmentSlotDefinition>();
    public IReadOnlyDictionary<string, SkillDefinition> Skills { get; init; } = new Dictionary<string, SkillDefinition>();
    public IReadOnlyDictionary<string, BuffDefinition> Buffs { get; init; } = new Dictionary<string, BuffDefinition>();
    public IReadOnlyDictionary<string, NpcDefinition> Npcs { get; init; } = new Dictionary<string, NpcDefinition>();
    public IReadOnlyDictionary<string, ZoneDefinition> Zones { get; init; } = new Dictionary<string, ZoneDefinition>();
    public IReadOnlyDictionary<string, DropTableDefinition> DropTables { get; init; } = new Dictionary<string, DropTableDefinition>();
    public IReadOnlyDictionary<string, EffectDefinition> Effects { get; init; } = new Dictionary<string, EffectDefinition>();
    public MovementDefinition Movement { get; init; } = new(6f, TimeSpan.FromSeconds(2));
}

[MessagePackObject]
[Orleans.GenerateSerializer(GenerateFieldIds = Orleans.GenerateFieldIds.PublicProperties)]
[Alias("GameServer.Contracts.InventoryStack")]
public sealed record InventoryStack([property: Key(0)] string ItemId, [property: Key(1)] int Quantity);
[MessagePackObject]
[Orleans.GenerateSerializer(GenerateFieldIds = Orleans.GenerateFieldIds.PublicProperties)]
[Alias("GameServer.Contracts.QuestProgress")]
public sealed record QuestProgress([property: Key(0)] string QuestId, [property: Key(1)] int Progress, [property: Key(2)] bool IsAccepted, [property: Key(3)] bool IsCompleted);
/// <summary>提供角色的完整对外状态，包括基础数据、资源、装备、Buff 与技能。</summary>
[MessagePackObject]
[Orleans.GenerateSerializer(GenerateFieldIds = Orleans.GenerateFieldIds.PublicProperties)]
[Alias("GameServer.Contracts.CharacterSnapshot")]
public sealed record CharacterSnapshot([property: Key(0)] string CharacterId, [property: Key(1)] string AccountId, [property: Key(2)] string Name, [property: Key(3)] string ZoneId, [property: Key(4)] string ContentVersion, [property: Key(5)] WorldPosition Position, [property: Key(6)] int Level, [property: Key(7)] IReadOnlyDictionary<string, decimal> Attributes, [property: Key(8)] IReadOnlyList<InventoryStack> Inventory, [property: Key(9)] IReadOnlyList<QuestProgress> Quests, [property: Key(10)] string ClassId, [property: Key(11)] IReadOnlyDictionary<string, decimal> Resources, [property: Key(12)] IReadOnlyList<EquippedItem> Equipment, [property: Key(13)] IReadOnlyList<ActiveBuff> Buffs, [property: Key(14)] IReadOnlyList<string> Skills);
[MessagePackObject]
[Orleans.GenerateSerializer(GenerateFieldIds = Orleans.GenerateFieldIds.PublicProperties)]
[Alias("GameServer.Contracts.EquippedItem")]
public sealed record EquippedItem([property: Key(0)] string SlotId, [property: Key(1)] string ItemId);
[MessagePackObject]
[Orleans.GenerateSerializer(GenerateFieldIds = Orleans.GenerateFieldIds.PublicProperties)]
[Alias("GameServer.Contracts.ActiveBuff")]
public sealed record ActiveBuff([property: Key(0)] string BuffId, [property: Key(1)] DateTimeOffset ExpiresAt);
[MessagePackObject]
[Orleans.GenerateSerializer(GenerateFieldIds = Orleans.GenerateFieldIds.PublicProperties)]
[Alias("GameServer.Contracts.ZoneEntity")]
public sealed record ZoneEntity([property: Key(0)] string EntityId, [property: Key(1)] string Kind, [property: Key(2)] WorldPosition Position, [property: Key(3)] decimal Health, [property: Key(4)] decimal MaxHealth);
[MessagePackObject]
[Orleans.GenerateSerializer(GenerateFieldIds = Orleans.GenerateFieldIds.PublicProperties)]
[Alias("GameServer.Contracts.ZoneSnapshot")]
public sealed record ZoneSnapshot([property: Key(0)] string ZoneId, [property: Key(1)] string ContentVersion, [property: Key(2)] IReadOnlyList<ZoneEntity> Entities);
[MessagePackObject]
[Orleans.GenerateSerializer(GenerateFieldIds = Orleans.GenerateFieldIds.PublicProperties)]
[Alias("GameServer.Contracts.NpcInteraction")]
public sealed record NpcInteraction([property: Key(0)] string NpcId, [property: Key(1)] IReadOnlyList<string> OfferedQuestIds, [property: Key(2)] IReadOnlyList<string> ReadyQuestIds);

[Orleans.GenerateSerializer(GenerateFieldIds = Orleans.GenerateFieldIds.PublicProperties)]
[Alias("GameServer.Contracts.AttackResult")]
public sealed record AttackResult(bool Accepted, string? ErrorCode, bool TargetDefeated, string? RewardItemId, int RewardCount, decimal TargetHealth) { public IReadOnlyList<InventoryStack> Rewards { get; init; } = []; }

[Orleans.GenerateSerializer(GenerateFieldIds = Orleans.GenerateFieldIds.PublicProperties)]
[Alias("GameServer.Contracts.CharacterSummary")]
public sealed record CharacterSummary(string CharacterId, string Name, int Level);

[Orleans.GenerateSerializer(GenerateFieldIds = Orleans.GenerateFieldIds.PublicProperties)]
[Alias("GameServer.Contracts.CreateCharacterResult")]
public sealed record CreateCharacterResult(bool Succeeded, string? ErrorCode, CharacterSummary? Character);

/// <summary>返回角色命令结果、当前完整快照及可选的战斗或 NPC 交互信息。</summary>
[Orleans.GenerateSerializer(GenerateFieldIds = Orleans.GenerateFieldIds.PublicProperties)]
[Alias("GameServer.Contracts.CommandResult")]
public sealed record CommandResult(bool Succeeded, string? ErrorCode, CharacterSnapshot? Snapshot = null, AttackResult? Attack = null) { public NpcInteraction? Interaction { get; init; } }

[Orleans.GenerateSerializer(GenerateFieldIds = Orleans.GenerateFieldIds.PublicProperties)]
[Alias("GameServer.Contracts.RewardGrant")]
public sealed record RewardGrant(string ItemId, int Quantity);
