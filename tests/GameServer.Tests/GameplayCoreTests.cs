using GameServer.Abstractions;
using GameServer.Contracts;
using GameServer.Domain.Gameplay;
using GameServer.Infrastructure;
using GameServer.Infrastructure.Content;
using GameServer.SampleGameplay;
using MessagePack;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace GameServer.Tests;

/// <summary>验证玩法模块注册、类型化内容、效果解析和协议兼容行为。</summary>
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
        using var provider = CreateProvider();
        var compiler = provider.GetRequiredService<JsonGameContentCompiler>();
        var version = compiler.Compile(GameContentDefaults.Create());
        var catalog = provider.GetRequiredService<IGameplayFeatureCatalog>();
        var effect = new EffectDefinition("strike", EffectKind.DamageMonster, 5) { ScalingAttributeId = "attack", ScalingFactor = 1.5m };
        var result = catalog.GetEffect(BuiltInEffectTypes.Resolve(effect)).Resolve(
            effect,
            new EffectContext(version, new Dictionary<string, decimal> { ["attack"] = 10 }));
        Assert.Equal(20m, result.MonsterDamage);
    }

    [Fact]
    public void Content_validation_rejects_unknown_effect()
    {
        using var provider = CreateProvider();
        var content = GameContentDefaults.Create() with
        {
            Classes = new Dictionary<string, CharacterClassDefinition>(),
            Skills = new Dictionary<string, SkillDefinition> { ["skill"] = new("skill", "Skill", 0, TimeSpan.Zero, "missing") }
        };
        var exception = Assert.Throws<ContentValidationException>(() => provider.GetRequiredService<JsonGameContentCompiler>().Compile(content));
        Assert.Contains(exception.Issues, issue => issue.Code == "unknown_effect");
    }

    [Fact]
    public void Content_validation_rejects_invalid_movement_budget()
    {
        using var provider = CreateProvider();
        var content = GameContentDefaults.Create() with { Movement = new MovementDefinition(0f, TimeSpan.FromSeconds(2)) };
        var exception = Assert.Throws<ContentValidationException>(() => provider.GetRequiredService<JsonGameContentCompiler>().Compile(content));
        Assert.Contains(exception.Issues, issue => issue.Code == "invalid_movement");
    }

    [Fact]
    public void Legacy_content_without_module_sections_uses_compatible_defaults()
    {
        const string legacyJson = """
            { "version": "legacy", "attributes": {}, "items": {}, "monsters": {}, "quests": {} }
            """;
        using var provider = CreateProvider();
        var version = provider.GetRequiredService<JsonGameContentCompiler>().Compile(legacyJson);
        Assert.Equal(12f, MovementRules.Capacity(version.Content.Movement));
        Assert.Equal(0.25m, version.GetModuleContent<VampirismContent>(VampirismModuleExtensions.ModuleId).HealingRatio);
    }

    [Fact]
    public void Default_content_has_a_complete_core_module_graph()
    {
        using var provider = CreateProvider();
        var version = provider.GetRequiredService<JsonGameContentCompiler>().Compile(GameContentDefaults.Create());
        Assert.Contains("basic-attack", version.Content.Classes["adventurer"].InitialSkillIds);
    }

    [Fact]
    public void Versioned_content_file_deserializes_and_passes_validation()
    {
        var path = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "GameServer.Gateway", "Content", "game-content.v1.json"));
        using var provider = CreateProvider();
        var version = provider.GetRequiredService<JsonGameContentCompiler>().Compile(File.ReadAllText(path));
        Assert.Equal(12f, MovementRules.Capacity(version.Content.Movement));
        Assert.Equal("health", version.GetModuleContent<VampirismContent>(VampirismModuleExtensions.ModuleId).HealingResourceId);
    }

    [Fact]
    public void Realtime_envelope_round_trips_skill_command()
    {
        var envelope = new RealtimeEnvelope { MessageId = RealtimeMessageIds.UseSkill, RequestId = "r2", OperationId = "o2", Payload = MessagePackSerializer.Serialize(new UseSkillCommand { SkillId = "power-strike", TargetMonsterId = "green-slime" }) };
        var restored = MessagePackSerializer.Deserialize<RealtimeEnvelope>(MessagePackSerializer.Serialize(envelope));
        Assert.Equal("power-strike", MessagePackSerializer.Deserialize<UseSkillCommand>(restored.Payload).SkillId);
    }

    [Fact]
    public void Realtime_snapshot_round_trips_complete_character_state()
    {
        var character = new CharacterSnapshot(
            "character-1", "account-1", "Hero", "starter-plains:v1", "v1", new WorldPosition(2, 3), 4,
            new Dictionary<string, decimal> { ["attack"] = 12m }, [new InventoryStack("sword", 1)],
            [new QuestProgress("slime-hunt", 1, true, false)], "adventurer",
            new Dictionary<string, decimal> { ["mana"] = 8m }, [new EquippedItem("main-hand", "sword")],
            [new ActiveBuff("power", DateTimeOffset.Parse("2026-09-07T00:00:00Z"))], ["power-strike"]);
        var payload = new CharacterSnapshotPayload
        {
            Character = character,
            Zone = new ZoneSnapshot("starter-plains:v1", "v1", []),
            Interaction = new NpcInteraction("guard-aria", ["slime-hunt"], [])
        };

        var restored = MessagePackSerializer.Deserialize<CharacterSnapshotPayload>(MessagePackSerializer.Serialize(payload));

        Assert.Equal("adventurer", restored.Character.ClassId);
        Assert.Equal(8m, restored.Character.Resources["mana"]);
        Assert.Equal("sword", restored.Character.Equipment.Single().ItemId);
        Assert.Equal("power", restored.Character.Buffs.Single().BuffId);
        Assert.Equal("power-strike", restored.Character.Skills.Single());
        Assert.Equal("guard-aria", restored.Interaction!.NpcId);
    }

    private static ServiceProvider CreateProvider()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] = "Host=localhost;Database=test;Username=test;Password=test"
        }).Build();
        var services = new ServiceCollection();
        services.AddGameServer(configuration).AddSampleVampirism();
        return services.BuildServiceProvider();
    }
}
