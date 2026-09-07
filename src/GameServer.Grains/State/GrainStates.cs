using GameServer.Contracts;

namespace GameServer.Grains.State;

/// <summary>保存账号拥有的角色列表。</summary>
[Orleans.GenerateSerializer(GenerateFieldIds = Orleans.GenerateFieldIds.PublicProperties)]
public sealed class AccountState
{
    public List<CharacterSummary> Characters { get; set; } = [];
}

/// <summary>保存角色权威状态以及跨 Grain 操作与账本同步的恢复记录。</summary>
[Orleans.GenerateSerializer(GenerateFieldIds = Orleans.GenerateFieldIds.PublicProperties)]
public sealed class CharacterState
{
    public string AccountId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string ClassId { get; set; } = string.Empty;
    public string ZoneId { get; set; } = "starter-plains:v1";
    public string ContentVersion { get; set; } = "v1";
    public WorldPosition Position { get; set; } = new(0, 0);
    public int Level { get; set; } = 1;
    public Dictionary<string, decimal> BaseAttributes { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, decimal> Resources { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> Inventory { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> EquippedItems { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, DateTimeOffset> ActiveBuffs { get; set; } = new(StringComparer.Ordinal);
    public HashSet<string> LearnedSkillIds { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, DateTimeOffset> SkillCooldowns { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, QuestProgress> Quests { get; set; } = new(StringComparer.Ordinal);
    public HashSet<string> ProcessedOperationIds { get; set; } = new(StringComparer.Ordinal);
    public DateTimeOffset? LastAttackAt { get; set; }
    public Dictionary<string, OperationReceipt> OperationReceipts { get; set; } = new(StringComparer.Ordinal);
    public List<PendingReward> PendingRewards { get; set; } = [];
    public PendingCombat? PendingCombat { get; set; }
    public PendingMove? PendingMove { get; set; }
    public PendingZoneTransfer? PendingZoneTransfer { get; set; }
    public MovementBudgetState? MovementBudget { get; set; }
}

/// <summary>保存命令指纹与业务结果；重试时返回当前角色快照。</summary>
[Orleans.GenerateSerializer(GenerateFieldIds = Orleans.GenerateFieldIds.PublicProperties)]
public sealed class OperationReceipt
{
    public string Fingerprint { get; set; } = string.Empty;
    public AttackResult? Attack { get; set; }
    public NpcInteraction? Interaction { get; set; }
    public string? ErrorCode { get; set; }
}

/// <summary>与物品变更一起提交的奖励发件箱，支持账本投递失败后的重复投递。</summary>
[Orleans.GenerateSerializer(GenerateFieldIds = Orleans.GenerateFieldIds.PublicProperties)]
public sealed class PendingReward
{
    public string OperationId { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public List<RewardGrant> Rewards { get; set; } = [];
}

/// <summary>在调用怪物前保存结算参数，重激活后继续同一次攻击。</summary>
[Orleans.GenerateSerializer(GenerateFieldIds = Orleans.GenerateFieldIds.PublicProperties)]
public sealed class PendingCombat
{
    public string OperationId { get; set; } = string.Empty;
    public string Fingerprint { get; set; } = string.Empty;
    public string MonsterId { get; set; } = string.Empty;
    public decimal Damage { get; set; }
    public decimal Range { get; set; }
    public string? SkillId { get; set; }
    public DateTimeOffset ExecutedAt { get; set; }
    public Dictionary<string, decimal> ResourceChanges { get; set; } = [];
    public List<string> BuffIds { get; set; } = [];
}

/// <summary>在更新区域坐标前保存目标位置与核准预算，防止恢复时重复计算或扣减。</summary>
[Orleans.GenerateSerializer(GenerateFieldIds = Orleans.GenerateFieldIds.PublicProperties)]
public sealed class PendingMove
{
    public string OperationId { get; set; } = string.Empty;
    public string Fingerprint { get; set; } = string.Empty;
    public WorldPosition Position { get; set; }
    public MovementBudgetState? ApprovedBudget { get; set; }
}

/// <summary>保存区域切换意图和切换前预算，成功后不因换区额外补充移动距离。</summary>
[Orleans.GenerateSerializer(GenerateFieldIds = Orleans.GenerateFieldIds.PublicProperties)]
public sealed class PendingZoneTransfer
{
    public string PreviousZoneId { get; set; } = string.Empty;
    public string TargetZoneId { get; set; } = string.Empty;
    public string ContentVersion { get; set; } = string.Empty;
    public bool Joined { get; set; }
    public MovementBudgetState? MovementBudget { get; set; }
}

/// <summary>保存最近一次服务器核准后的剩余移动距离及其持久化时间锚点。</summary>
[Orleans.GenerateSerializer(GenerateFieldIds = Orleans.GenerateFieldIds.PublicProperties)]
public sealed class MovementBudgetState
{
    public float AvailableDistance { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

/// <summary>保存区域固定内容版本及成员位置。</summary>
[Orleans.GenerateSerializer(GenerateFieldIds = Orleans.GenerateFieldIds.PublicProperties)]
public sealed class ZoneState
{
    public string ContentVersion { get; set; } = string.Empty; public Dictionary<string, WorldPosition> Players { get; set; } = new(StringComparer.Ordinal);
}
/// <summary>将怪物生命值、重生时间和攻击结果作为同一状态持久化。</summary>
[Orleans.GenerateSerializer(GenerateFieldIds = Orleans.GenerateFieldIds.PublicProperties)]
public sealed class MonsterState
{
    public MonsterDefinition? Definition { get; set; }
    public decimal Health { get; set; }
    public DateTimeOffset? RespawnAt { get; set; }
    public HashSet<string> ProcessedOperationIds { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, AttackResult> OperationResults { get; set; } = new(StringComparer.Ordinal);
}
