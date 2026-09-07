using GameServer.Contracts;
using GameServer.Domain.Attributes;
using GameServer.Domain.Gameplay;
using MessagePack;

namespace GameServer.Tests;

/// <summary>验证纯领域规则的数值边界、状态迁移和协议往返行为。</summary>
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
    public void Movement_budget_refills_from_server_elapsed_time()
    {
        var definition = new MovementDefinition(6f, TimeSpan.FromSeconds(2));
        var result = MovementRules.Evaluate(new WorldPosition(0, 0), new WorldPosition(6, 0), 0f, TimeSpan.FromSeconds(1), definition);
        Assert.True(result.Allowed);
        Assert.Equal(0f, result.AvailableDistance);
    }

    [Fact]
    public void Movement_budget_distinguishes_impossible_distance_from_temporary_rate_limit()
    {
        var definition = new MovementDefinition(6f, TimeSpan.FromSeconds(2));
        var rateLimited = MovementRules.Evaluate(new WorldPosition(0, 0), new WorldPosition(1, 0), 0f, TimeSpan.Zero, definition);
        var tinyRateLimited = MovementRules.Evaluate(new WorldPosition(0, 0), new WorldPosition(0.00001f, 0), 0f, TimeSpan.Zero, definition);
        var impossible = MovementRules.Evaluate(new WorldPosition(0, 0), new WorldPosition(13, 0), 12f, TimeSpan.Zero, definition);
        Assert.Equal("movement_rate_limited", rateLimited.ErrorCode);
        Assert.Equal("movement_rate_limited", tinyRateLimited.ErrorCode);
        Assert.Equal("invalid_movement", impossible.ErrorCode);
    }

    [Fact]
    public void Movement_budget_caps_long_pauses_and_ignores_clock_rollback()
    {
        var definition = new MovementDefinition(6f, TimeSpan.FromSeconds(2));
        var capped = MovementRules.Evaluate(new WorldPosition(0, 0), new WorldPosition(12, 0), 0f, TimeSpan.FromDays(1), definition);
        var rollback = MovementRules.Evaluate(new WorldPosition(0, 0), new WorldPosition(1, 0), 0f, TimeSpan.FromSeconds(-1), definition);
        Assert.True(capped.Allowed);
        Assert.Equal(0f, capped.AvailableDistance);
        Assert.Equal("movement_rate_limited", rollback.ErrorCode);
    }

    [Fact]
    public void Movement_rejects_non_finite_coordinates()
    {
        var definition = new MovementDefinition(6f, TimeSpan.FromSeconds(2));
        var result = MovementRules.Evaluate(new WorldPosition(0, 0), new WorldPosition(float.NaN, 0), 12f, TimeSpan.Zero, definition);
        Assert.Equal("invalid_movement", result.ErrorCode);
    }

    [Fact]
    public void Realtime_envelope_round_trips_move_command()
    {
        var envelope = new RealtimeEnvelope { MessageId = RealtimeMessageIds.Move, RequestId = "r1", OperationId = "o1", Payload = MessagePackSerializer.Serialize(new MoveCommand { X = 2, Y = 3 }) };
        var restored = MessagePackSerializer.Deserialize<RealtimeEnvelope>(MessagePackSerializer.Serialize(envelope));
        Assert.Equal(2, MessagePackSerializer.Deserialize<MoveCommand>(restored.Payload).X);
    }

    [Fact]
    public void Realtime_message_ids_remain_wire_compatible()
    {
        Assert.Equal(100, RealtimeMessageIds.Move);
        Assert.Equal(101, RealtimeMessageIds.Attack);
        Assert.Equal(102, RealtimeMessageIds.AcceptQuest);
        Assert.Equal(103, RealtimeMessageIds.CompleteQuest);
        Assert.Equal(104, RealtimeMessageIds.UseSkill);
        Assert.Equal(105, RealtimeMessageIds.Equip);
        Assert.Equal(106, RealtimeMessageIds.Unequip);
        Assert.Equal(107, RealtimeMessageIds.InteractNpc);
        Assert.Equal(200, RealtimeMessageIds.Snapshot);
        Assert.Equal(900, RealtimeMessageIds.Error);
    }
}
