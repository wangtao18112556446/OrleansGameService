using MessagePack;
namespace GameServer.Contracts;
public static class RealtimeMessageIds { public const int Move = 100; public const int Attack = 101; public const int AcceptQuest = 102; public const int CompleteQuest = 103; public const int UseSkill = 104; public const int Equip = 105; public const int Unequip = 106; public const int InteractNpc = 107; public const int Snapshot = 200; public const int SnapshotV2 = 201; public const int Error = 900; }
[MessagePackObject] public sealed class RealtimeEnvelope { [Key(0)] public required int MessageId { get; init; } [Key(1)] public required string RequestId { get; init; } [Key(2)] public required string OperationId { get; init; } [Key(3)] public required byte[] Payload { get; init; } [Key(4)] public int ProtocolVersion { get; init; } }
[MessagePackObject] public sealed class MoveCommand { [Key(0)] public required float X { get; init; } [Key(1)] public required float Y { get; init; } }
[MessagePackObject] public sealed class AttackCommand { [Key(0)] public required string TargetMonsterId { get; init; } }
[MessagePackObject] public sealed class QuestCommand { [Key(0)] public required string QuestId { get; init; } [Key(1)] public string? NpcId { get; init; } }
[MessagePackObject] public sealed class UseSkillCommand { [Key(0)] public required string SkillId { get; init; } [Key(1)] public string? TargetMonsterId { get; init; } }
[MessagePackObject] public sealed class EquipCommand { [Key(0)] public required string SlotId { get; init; } [Key(1)] public required string ItemId { get; init; } }
[MessagePackObject] public sealed class UnequipCommand { [Key(0)] public required string SlotId { get; init; } }
[MessagePackObject] public sealed class InteractNpcCommand { [Key(0)] public required string NpcId { get; init; } }
[MessagePackObject] public sealed class ErrorPayload { [Key(0)] public required string Code { get; init; } [Key(1)] public required string Message { get; init; } }
[MessagePackObject] public sealed class CharacterSnapshotPayload { [Key(0)] public required CharacterSnapshot Character { get; init; } [Key(1)] public required ZoneSnapshot Zone { get; init; } }
[MessagePackObject] public sealed class CharacterSnapshotPayloadV2 { [Key(0)] public required CharacterSnapshotV2 Character { get; init; } [Key(1)] public required ZoneSnapshotV2 Zone { get; init; } [Key(2)] public NpcInteraction? Interaction { get; init; } }
