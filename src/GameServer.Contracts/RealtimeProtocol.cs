using MessagePack;
namespace GameServer.Contracts;

/// <summary>集中维护当前实时协议使用的消息编号。</summary>
public static class RealtimeMessageIds { public const int Move = 100; public const int Attack = 101; public const int AcceptQuest = 102; public const int CompleteQuest = 103; public const int UseSkill = 104; public const int Equip = 105; public const int Unequip = 106; public const int InteractNpc = 107; public const int Snapshot = 200; public const int Error = 900; }

/// <summary>封装实时命令及请求、操作关联标识。</summary>
[MessagePackObject]
[Orleans.GenerateSerializer(GenerateFieldIds = Orleans.GenerateFieldIds.PublicProperties)]
public sealed class RealtimeEnvelope { [Key(0)] public required int MessageId { get; init; } [Key(1)] public required string RequestId { get; init; } [Key(2)] public required string OperationId { get; init; } [Key(3)] public required byte[] Payload { get; init; } }

/// <summary>携带客户端请求的目标位置，由服务器校验移动。</summary>
[MessagePackObject]
[Orleans.GenerateSerializer(GenerateFieldIds = Orleans.GenerateFieldIds.PublicProperties)]
public sealed class MoveCommand { [Key(0)] public required float X { get; init; } [Key(1)] public required float Y { get; init; } }

/// <summary>指定普通攻击的怪物目标。</summary>
[MessagePackObject]
[Orleans.GenerateSerializer(GenerateFieldIds = Orleans.GenerateFieldIds.PublicProperties)]
public sealed class AttackCommand { [Key(0)] public required string TargetMonsterId { get; init; } }

/// <summary>指定接取或完成的任务及可选 NPC。</summary>
[MessagePackObject]
[Orleans.GenerateSerializer(GenerateFieldIds = Orleans.GenerateFieldIds.PublicProperties)]
public sealed class QuestCommand { [Key(0)] public required string QuestId { get; init; } [Key(1)] public string? NpcId { get; init; } }

/// <summary>指定技能和可选目标。</summary>
[MessagePackObject]
[Orleans.GenerateSerializer(GenerateFieldIds = Orleans.GenerateFieldIds.PublicProperties)]
public sealed class UseSkillCommand { [Key(0)] public required string SkillId { get; init; } [Key(1)] public string? TargetMonsterId { get; init; } }

/// <summary>指定待装备物品及装备槽。</summary>
[MessagePackObject]
[Orleans.GenerateSerializer(GenerateFieldIds = Orleans.GenerateFieldIds.PublicProperties)]
public sealed class EquipCommand { [Key(0)] public required string SlotId { get; init; } [Key(1)] public required string ItemId { get; init; } }

/// <summary>指定需要卸下装备的槽位。</summary>
[MessagePackObject]
[Orleans.GenerateSerializer(GenerateFieldIds = Orleans.GenerateFieldIds.PublicProperties)]
public sealed class UnequipCommand { [Key(0)] public required string SlotId { get; init; } }

/// <summary>指定需要进行距离校验的 NPC。</summary>
[MessagePackObject]
[Orleans.GenerateSerializer(GenerateFieldIds = Orleans.GenerateFieldIds.PublicProperties)]
public sealed class InteractNpcCommand { [Key(0)] public required string NpcId { get; init; } }

/// <summary>返回稳定错误码及客户端可理解的说明。</summary>
[MessagePackObject]
[Orleans.GenerateSerializer(GenerateFieldIds = Orleans.GenerateFieldIds.PublicProperties)]
public sealed class ErrorPayload { [Key(0)] public required string Code { get; init; } [Key(1)] public required string Message { get; init; } }

/// <summary>提供完整角色、区域及可选 NPC 交互结果。</summary>
[MessagePackObject]
[Orleans.GenerateSerializer(GenerateFieldIds = Orleans.GenerateFieldIds.PublicProperties)]
public sealed class CharacterSnapshotPayload { [Key(0)] public required CharacterSnapshot Character { get; init; } [Key(1)] public required ZoneSnapshot Zone { get; init; } [Key(2)] public NpcInteraction? Interaction { get; init; } }
