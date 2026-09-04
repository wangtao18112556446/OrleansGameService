using GameServer.Contracts;
using GameServer.Domain.Attributes;
using GameServer.Domain.Gameplay;
using MessagePack;

namespace GameServer.Tests;

public sealed class GameplayRulesTests
{
    [Fact]
    public void Attribute_set_applies_flat_then_percentage_and_limit()
    {
        var set = new AttributeSet(new Dictionary<string, AttributeDefinition> { ["attack"] = new("attack", 10, 0, 20) });
        set.AddModifier(new AttributeModifier("attack", 5, 0, "sword"));
        set.AddModifier(new AttributeModifier("attack", 0, .5m, "buff"));
        Assert.Equal(20m, set.Get("attack"));
    }

    [Fact]
    public void Quest_can_only_complete_after_required_kills()
    {
        var quest = new QuestDefinition("hunt", "npc", "slime", 2, "token", 1);
        var progress = QuestStateMachine.Accept(null, quest.Id);
        Assert.False(QuestStateMachine.CanComplete(progress, quest));
        progress = QuestStateMachine.RecordKill(progress, quest);
        progress = QuestStateMachine.RecordKill(progress, quest);
        Assert.True(QuestStateMachine.CanComplete(progress, quest));
    }

    [Fact]
    public void Movement_rejects_teleport()
    {
        Assert.False(CombatRules.ValidateMove(new WorldPosition(0, 0), new WorldPosition(13, 0), 12).Allowed);
    }

    [Fact]
    public void Realtime_envelope_round_trips_with_versioned_payload()
    {
        var envelope = new RealtimeEnvelope { MessageId = RealtimeMessageIds.Move, RequestId = "r1", OperationId = "o1", ProtocolVersion = 1, Payload = MessagePackSerializer.Serialize(new MoveCommand { X = 2, Y = 3 }) };
        var restored = MessagePackSerializer.Deserialize<RealtimeEnvelope>(MessagePackSerializer.Serialize(envelope));
        Assert.Equal(1, restored.ProtocolVersion);
        Assert.Equal(2, MessagePackSerializer.Deserialize<MoveCommand>(restored.Payload).X);
    }
}
