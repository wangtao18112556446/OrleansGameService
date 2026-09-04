using GameServer.Contracts;

namespace GameServer.Grains.State;

public sealed class AccountState { public List<CharacterSummary> Characters { get; set; } = []; }
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
}
public sealed class ZoneState { public string ContentVersion { get; set; } = string.Empty; public Dictionary<string, WorldPosition> Players { get; set; } = new(StringComparer.Ordinal); }
public sealed class MonsterState { public MonsterDefinition? Definition { get; set; } public decimal Health { get; set; } public DateTimeOffset? RespawnAt { get; set; } public HashSet<string> ProcessedOperationIds { get; set; } = new(StringComparer.Ordinal); public Dictionary<string, AttackResult> OperationResults { get; set; } = new(StringComparer.Ordinal); }
