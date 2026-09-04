using GameServer.Contracts;
using GameServer.Domain.Gameplay;
using GameServer.Infrastructure.Content;
using MessagePack;

namespace GameServer.Tests;

public sealed class GameplayCoreTests
{
    [Fact]
    public void Inventory_capacity_accounts_for_stack_sizes()
    {
        var items = new Dictionary<string, ItemDefinition> { ["gel"] = new("gel", "Gel", 10) };
        var inventory = new Dictionary<string, int> { ["gel"] = 230 };
        Assert.True(InventoryRules.CanReceive(inventory, [new RewardGrant("gel", 10)], items, 24));
        Assert.False(InventoryRules.CanReceive(inventory, [new RewardGrant("gel", 11)], items, 24));
    }

    [Fact]
    public void Built_in_damage_effect_scales_from_attributes()
    {
        var registry = GameplayCore.CreateRegistry();
        var effect = new EffectDefinition("strike", EffectKind.DamageMonster, 5) { ScalingAttributeId = "attack", ScalingFactor = 1.5m };
        var result = registry.GetEffect(effect.Kind).Resolve(effect, new EffectContext(new Dictionary<string, decimal> { ["attack"] = 10 }));
        Assert.Equal(20m, result.MonsterDamage);
    }

    [Fact]
    public void Content_validation_rejects_unknown_effect()
    {
        var content = new GameContent("test", new Dictionary<string, AttributeDefinition> { ["attack"] = new("attack", 1) }, new Dictionary<string, ItemDefinition>(), new Dictionary<string, MonsterDefinition>(), new Dictionary<string, QuestDefinition>())
        {
            Skills = new Dictionary<string, SkillDefinition> { ["skill"] = new("skill", "Skill", 0, TimeSpan.Zero, "missing") }
        };
        Assert.Throws<InvalidOperationException>(() => JsonGameContentCatalog.Validate(content));
    }

    [Fact]
    public void Default_content_has_a_complete_core_module_graph()
    {
        var content = GameContentDefaults.Create();
        JsonGameContentCatalog.Validate(content);
        Assert.Contains("basic-attack", content.Classes["adventurer"].InitialSkillIds);
    }

    [Fact]
    public void Version_two_envelope_round_trips_new_command()
    {
        var envelope = new RealtimeEnvelope { MessageId = RealtimeMessageIds.UseSkill, RequestId = "r2", OperationId = "o2", ProtocolVersion = 2, Payload = MessagePackSerializer.Serialize(new UseSkillCommand { SkillId = "power-strike", TargetMonsterId = "green-slime" }) };
        var restored = MessagePackSerializer.Deserialize<RealtimeEnvelope>(MessagePackSerializer.Serialize(envelope));
        Assert.Equal(2, restored.ProtocolVersion);
        Assert.Equal("power-strike", MessagePackSerializer.Deserialize<UseSkillCommand>(restored.Payload).SkillId);
    }
}
