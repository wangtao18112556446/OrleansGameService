using MessagePack;

namespace GameServer.Contracts;

[MessagePackObject]
public readonly record struct WorldPosition([property: Key(0)] float X, [property: Key(1)] float Y)
{
    public float DistanceTo(WorldPosition other) => MathF.Sqrt(MathF.Pow(X - other.X, 2) + MathF.Pow(Y - other.Y, 2));
}

public sealed record AttributeDefinition(string Id, decimal DefaultValue, decimal? Minimum = null, decimal? Maximum = null);
public sealed record AttributeModifier(string AttributeId, decimal FlatAmount, decimal PercentAmount, string Source);
public sealed record StartingItem(string ItemId, int Quantity);
public sealed record CharacterClassDefinition(string Id, string DisplayName, IReadOnlyDictionary<string, decimal> InitialAttributes)
{
    public IReadOnlyList<string> InitialSkillIds { get; init; } = [];
    public IReadOnlyList<StartingItem> InitialItems { get; init; } = [];
}
public sealed record ResourceDefinition(string Id, string DisplayName, string MaximumAttributeId) { public decimal InitialValue { get; init; } }
public sealed record ItemDefinition(string Id, string DisplayName, int MaxStack = 99)
{
    public IReadOnlySet<string> Tags { get; init; } = new HashSet<string>(StringComparer.Ordinal);
    public IReadOnlyList<AttributeModifier> Modifiers { get; init; } = [];
}
public sealed record EquipmentSlotDefinition(string Id, string DisplayName, IReadOnlySet<string> AcceptedItemTags);
public enum SkillTargetKind { Self, Monster }
public sealed record SkillDefinition(string Id, string DisplayName, decimal ResourceCost, TimeSpan Cooldown, string EffectId)
{
    public string? ResourceId { get; init; }
    public SkillTargetKind TargetKind { get; init; } = SkillTargetKind.Monster;
    public IReadOnlyList<string> EffectIds { get; init; } = [];
}
public sealed record BuffDefinition(string Id, string DisplayName, TimeSpan Duration, IReadOnlyList<AttributeModifier> Modifiers);
public sealed record NpcDefinition(string Id, string DisplayName, WorldPosition Position, IReadOnlyList<string> QuestIds)
{
    public string ZoneId { get; init; } = "starter-plains";
    public decimal InteractionRange { get; init; } = 3m;
}
public sealed record ZoneDefinition(string Id, string DisplayName, int Capacity);
public sealed record DropEntry(string ItemId, int Quantity, decimal Probability);
public sealed record DropTableDefinition(string Id, IReadOnlyList<DropEntry> Entries);
public sealed record MonsterDefinition(string Id, string DisplayName, decimal Health, decimal Attack, WorldPosition SpawnPosition, string DropItemId, int DropCount) { public string? DropTableId { get; init; } }
public sealed record QuestDefinition(string Id, string NpcId, string TargetMonsterId, int RequiredKills, string RewardItemId, int RewardCount);
public enum EffectKind { DamageMonster, RestoreResource, ConsumeResource, ApplyBuff }
public sealed record EffectDefinition(string Id, EffectKind Kind, decimal Amount = 0m)
{
    public string? ResourceId { get; init; }
    public string? BuffId { get; init; }
    public string? ScalingAttributeId { get; init; }
    public decimal ScalingFactor { get; init; }
}

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
}

public interface IGameContentCatalog { GameContent GetVersion(string version); GameContent GetActive(); void Activate(GameContent content); IReadOnlyList<GameContent> GetAll(); }

[MessagePackObject] public sealed record InventoryStack([property: Key(0)] string ItemId, [property: Key(1)] int Quantity);
[MessagePackObject] public sealed record QuestProgress([property: Key(0)] string QuestId, [property: Key(1)] int Progress, [property: Key(2)] bool IsAccepted, [property: Key(3)] bool IsCompleted);
[MessagePackObject] public sealed record CharacterSnapshot([property: Key(0)] string CharacterId, [property: Key(1)] string AccountId, [property: Key(2)] string Name, [property: Key(3)] string ZoneId, [property: Key(4)] string ContentVersion, [property: Key(5)] WorldPosition Position, [property: Key(6)] int Level, [property: Key(7)] IReadOnlyDictionary<string, decimal> Attributes, [property: Key(8)] IReadOnlyList<InventoryStack> Inventory, [property: Key(9)] IReadOnlyList<QuestProgress> Quests);
[MessagePackObject] public sealed record EquippedItem([property: Key(0)] string SlotId, [property: Key(1)] string ItemId);
[MessagePackObject] public sealed record ActiveBuff([property: Key(0)] string BuffId, [property: Key(1)] DateTimeOffset ExpiresAt);
[MessagePackObject] public sealed record CharacterSnapshotV2([property: Key(0)] CharacterSnapshot Character, [property: Key(1)] string ClassId, [property: Key(2)] IReadOnlyDictionary<string, decimal> Resources, [property: Key(3)] IReadOnlyList<EquippedItem> Equipment, [property: Key(4)] IReadOnlyList<ActiveBuff> Buffs, [property: Key(5)] IReadOnlyList<string> Skills);
[MessagePackObject] public sealed record ZoneEntity([property: Key(0)] string EntityId, [property: Key(1)] string Kind, [property: Key(2)] WorldPosition Position, [property: Key(3)] decimal Health, [property: Key(4)] decimal MaxHealth);
[MessagePackObject] public sealed record ZoneSnapshot([property: Key(0)] string ZoneId, [property: Key(1)] string ContentVersion, [property: Key(2)] IReadOnlyList<ZoneEntity> Entities);
[MessagePackObject] public sealed record NpcInteraction([property: Key(0)] string NpcId, [property: Key(1)] IReadOnlyList<string> OfferedQuestIds, [property: Key(2)] IReadOnlyList<string> ReadyQuestIds);
[MessagePackObject] public sealed record ZoneSnapshotV2([property: Key(0)] ZoneSnapshot Zone, [property: Key(1)] IReadOnlyList<NpcInteraction> Interactions);
public sealed record AttackResult(bool Accepted, string? ErrorCode, bool TargetDefeated, string? RewardItemId, int RewardCount, decimal TargetHealth) { public IReadOnlyList<InventoryStack> Rewards { get; init; } = []; }
public sealed record CharacterSummary(string CharacterId, string Name, int Level);
public sealed record CreateCharacterResult(bool Succeeded, string? ErrorCode, CharacterSummary? Character);
public sealed record CommandResult(bool Succeeded, string? ErrorCode, CharacterSnapshot? Snapshot = null, AttackResult? Attack = null) { public CharacterSnapshotV2? SnapshotV2 { get; init; } public NpcInteraction? Interaction { get; init; } }
public sealed record RewardGrant(string ItemId, int Quantity);
public interface IRewardLedger { Task<bool> TryRecordAsync(string characterId, string operationId, string kind, IReadOnlyList<RewardGrant> rewards, CancellationToken cancellationToken = default); }
