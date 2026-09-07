using GameServer.Contracts;
using MessagePack;

namespace GameServer.Tests;

/// <summary>使用固定历史字节验证协议兼容，避免序列化和反序列化同时改变而掩盖破坏性改动。</summary>
public sealed class RealtimeCompatibilityTests
{
    [Fact]
    public void Version_one_move_preserves_golden_wire_bytes()
    {
        const string golden = "9564A27231A26F31C40B92CA40000000CA4040000001";
        var envelope = MessagePackSerializer.Deserialize<RealtimeEnvelope>(Convert.FromHexString(golden));
        var move = MessagePackSerializer.Deserialize<MoveCommand>(envelope.Payload);
        Assert.Equal(1, envelope.ProtocolVersion);
        Assert.Equal(RealtimeMessageIds.Move, envelope.MessageId);
        Assert.Equal(2, move.X);
        Assert.Equal(3, move.Y);
        Assert.Equal(golden, Convert.ToHexString(MessagePackSerializer.Serialize(envelope)));
        Assert.Equal(envelope.Payload, MessagePackSerializer.Serialize(new MoveCommand { X = 2, Y = 3 }));
    }

    [Fact]
    public void Version_two_skill_preserves_golden_wire_bytes()
    {
        const string golden = "9568A27232A26F32C40592A173A16D02";
        var envelope = MessagePackSerializer.Deserialize<RealtimeEnvelope>(Convert.FromHexString(golden));
        var skill = MessagePackSerializer.Deserialize<UseSkillCommand>(envelope.Payload);
        Assert.Equal(2, envelope.ProtocolVersion);
        Assert.Equal(RealtimeMessageIds.UseSkill, envelope.MessageId);
        Assert.Equal("s", skill.SkillId);
        Assert.Equal("m", skill.TargetMonsterId);
        Assert.Equal(golden, Convert.ToHexString(MessagePackSerializer.Serialize(envelope)));
        Assert.Equal(envelope.Payload, MessagePackSerializer.Serialize(new UseSkillCommand { SkillId = "s", TargetMonsterId = "m" }));
    }
}
