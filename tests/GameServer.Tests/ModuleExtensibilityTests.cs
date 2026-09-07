using System.Text.Json;
using System.Text.Json.Nodes;
using GameServer.Abstractions;
using GameServer.Contracts;
using GameServer.Domain.Gameplay;
using GameServer.Infrastructure;
using GameServer.Infrastructure.Content;
using GameServer.SampleGameplay;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace GameServer.Tests;

/// <summary>通过公共模块接口验证依赖排序、冲突诊断、类型化内容和示例扩展效果。</summary>
public sealed class ModuleExtensibilityTests
{
    [Fact]
    public void Module_graph_is_stably_topologically_ordered()
    {
        var modules = new GameModuleCollection();
        var builder = new GameServerBuilder(new ServiceCollection(), new ConfigurationBuilder().Build(), modules);
        builder.AddModule(new GameModuleDescriptor("feature", ["foundation"]), _ => { });
        builder.AddModule(new GameModuleDescriptor("foundation"), _ => { });

        Assert.Equal(["foundation", "feature"], modules.Freeze().Select(module => module.Id));
    }

    [Fact]
    public void Duplicate_missing_and_cyclic_modules_are_rejected()
    {
        var duplicate = CreateBareBuilder(out _);
        duplicate.AddModule(new GameModuleDescriptor("same"), _ => { });
        Assert.Throws<InvalidOperationException>(() => duplicate.AddModule(new GameModuleDescriptor("same"), _ => { }));

        var missing = CreateBareBuilder(out var missingModules);
        missing.AddModule(new GameModuleDescriptor("child", ["missing"]), _ => { });
        Assert.Contains("missing", Assert.Throws<InvalidOperationException>(() => missingModules.Freeze()).Message);

        var cyclic = CreateBareBuilder(out var cyclicModules);
        cyclic.AddModule(new GameModuleDescriptor("a", ["b"]), _ => { });
        cyclic.AddModule(new GameModuleDescriptor("b", ["a"]), _ => { });
        Assert.Contains("cycle", Assert.Throws<InvalidOperationException>(() => cyclicModules.Freeze()).Message);
    }

    [Fact]
    public void Duplicate_effect_type_ids_are_rejected_when_catalog_is_built()
    {
        using var provider = CreateProvider(builder =>
        {
            builder.AddModule(new GameModuleDescriptor("first", ["core.gameplay"]), module => module.AddEffect<FirstDuplicateEffect>());
            builder.AddModule(new GameModuleDescriptor("second", ["core.gameplay"]), module => module.AddEffect<SecondDuplicateEffect>());
        });
        var exception = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IGameplayFeatureCatalog>());
        Assert.Contains("'first'", exception.Message);
        Assert.Contains("'second'", exception.Message);
    }

    [Fact]
    public void Unknown_required_and_malformed_module_sections_have_paths()
    {
        using var normal = CreateProvider(builder => builder.AddSampleVampirism());
        var unknownJson = AddModules(GameContentDefaults.Create(), new JsonObject { ["unknown.module"] = new JsonObject() });
        var unknown = Assert.Throws<ContentValidationException>(() => normal.GetRequiredService<JsonGameContentCompiler>().Compile(unknownJson));
        Assert.Contains(unknown.Issues, issue => issue.Code == "unknown_module" && issue.Path == "$.modules.unknown.module");

        using var required = CreateProvider(builder => builder.AddModule(
            new GameModuleDescriptor("required.module", ["core.gameplay"]),
            module => module.AddContentSection<RequiredModuleContent>(required: true)));
        var missing = Assert.Throws<ContentValidationException>(() => required.GetRequiredService<JsonGameContentCompiler>().Compile(GameContentDefaults.Create()));
        Assert.Contains(missing.Issues, issue => issue.Code == "missing_module_content" && issue.ModuleId == "required.module");

        var malformedJson = AddModules(GameContentDefaults.Create(), new JsonObject
        {
            [VampirismModuleExtensions.ModuleId] = new JsonObject { ["healingRatio"] = "not-a-number" }
        });
        var malformed = Assert.Throws<ContentValidationException>(() => normal.GetRequiredService<JsonGameContentCompiler>().Compile(malformedJson));
        Assert.Contains(malformed.Issues, issue => issue.Code == "invalid_module_content" && issue.Path.StartsWith("$.modules.sample.vampirism", StringComparison.Ordinal));
    }

    [Fact]
    public void Sample_module_resolves_damage_and_healing_without_core_changes()
    {
        using var provider = CreateProvider(builder => builder.AddSampleVampirism());
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "GameServer.Gateway", "Content", "game-content.v1.json"));
        var version = provider.GetRequiredService<JsonGameContentCompiler>().Compile(File.ReadAllText(path));
        var effect = version.Content.Effects["vampiric-strike-damage"];
        var result = provider.GetRequiredService<IGameplayFeatureCatalog>()
            .GetEffect(BuiltInEffectTypes.Resolve(effect))
            .Resolve(effect, new EffectContext(version, new Dictionary<string, decimal> { ["attack"] = 20m }, "green-slime"));

        Assert.Equal(12m, result.MonsterDamage);
        Assert.Equal(3m, result.ResourceChangesOrEmpty["health"]);
    }

    private static GameServerBuilder CreateBareBuilder(out GameModuleCollection modules)
    {
        modules = new GameModuleCollection();
        return new GameServerBuilder(new ServiceCollection(), new ConfigurationBuilder().Build(), modules);
    }

    private static ServiceProvider CreateProvider(Action<GameServerBuilder> configure)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] = "Host=localhost;Database=test;Username=test;Password=test"
        }).Build();
        var services = new ServiceCollection();
        var builder = services.AddGameServer(configuration);
        configure(builder);
        return services.BuildServiceProvider();
    }

    private static string AddModules(GameContent content, JsonObject modules)
    {
        var root = JsonSerializer.SerializeToNode(content, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.AsObject();
        root["modules"] = modules;
        return root.ToJsonString();
    }

    /// <summary>为必需内容节测试提供最小类型。</summary>
    private sealed class RequiredModuleContent;

    /// <summary>提供第一个重复效果实现。</summary>
    private sealed class FirstDuplicateEffect : DuplicateEffect
    {
        public FirstDuplicateEffect() { }
    }

    /// <summary>提供第二个重复效果实现。</summary>
    private sealed class SecondDuplicateEffect : DuplicateEffect
    {
        public SecondDuplicateEffect() { }
    }

    /// <summary>用相同类型标识模拟两个模块的效果冲突。</summary>
    private abstract class DuplicateEffect : IGameEffect
    {
        public string TypeId => "duplicate";
        public IEnumerable<ContentValidationIssue> Validate(EffectDefinition effect, GameContentVersion content) => [];
        public EffectResolution Resolve(EffectDefinition effect, EffectContext context) => new();
    }
}
