using GameServer.Contracts;
using GameServer.Domain.Gameplay;
using GameServer.Infrastructure.Content;
using MessagePack;

namespace GameServer.Tests;

/// <summary>验证玩法模块注册、内容引用和基础库存行为。</summary>
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
    public void Content_validation_rejects_invalid_movement_budget()
    {
        var content = GameContentDefaults.Create() with { Movement = new MovementDefinition(0f, TimeSpan.FromSeconds(2)) };
        Assert.Throws<InvalidOperationException>(() => JsonGameContentCatalog.Validate(content));
    }

    [Fact]
    public void Legacy_content_without_movement_definition_uses_compatible_defaults()
    {
        const string legacyJson = """
            { "version": "legacy", "attributes": {}, "items": {}, "monsters": {}, "quests": {} }
            """;
        var content = JsonGameContentCatalog.Deserialize(legacyJson);
        JsonGameContentCatalog.Validate(content);
        Assert.Equal(12f, MovementRules.Capacity(content.Movement));
    }

    [Fact]
    public void Default_content_has_a_complete_core_module_graph()
    {
        var content = GameContentDefaults.Create();
        JsonGameContentCatalog.Validate(content);
        Assert.Contains("basic-attack", content.Classes["adventurer"].InitialSkillIds);
    }

    [Fact]
    public void Versioned_content_file_deserializes_and_passes_validation()
    {
        var path = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "GameServer.Gateway", "Content", "game-content.v1.json"));
        var content = JsonGameContentCatalog.Deserialize(File.ReadAllText(path));
        JsonGameContentCatalog.Validate(content);
        Assert.Equal(12f, MovementRules.Capacity(content.Movement));
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
