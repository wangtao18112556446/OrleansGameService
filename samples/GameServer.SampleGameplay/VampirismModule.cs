using GameServer.Abstractions;
using GameServer.Contracts;

namespace GameServer.SampleGameplay;

/// <summary>定义示例吸血玩法在每个内容版本中的治疗资源和吸血比例。</summary>
public sealed class VampirismContent
{
    public string HealingResourceId { get; init; } = "health";
    public decimal HealingRatio { get; init; } = 0.25m;
}

/// <summary>解析示例吸血伤害，并根据当前内容版本配置同步恢复角色资源。</summary>
public sealed class VampiricDamageEffect : IGameEffect
{
    public const string EffectTypeId = "sample.vampirism.damage";
    public string TypeId => EffectTypeId;

    public IEnumerable<ContentValidationIssue> Validate(EffectDefinition effect, GameContentVersion content)
    {
        if (!content.TryGetModuleContent<VampirismContent>(VampirismModuleExtensions.ModuleId, out _))
            yield return new($"$.effects.{effect.Id}.typeId", "missing_module_content", "Vampirism content is unavailable.", VampirismModuleExtensions.ModuleId);
    }

    public EffectResolution Resolve(EffectDefinition effect, EffectContext context)
    {
        var options = context.ContentVersion.GetModuleContent<VampirismContent>(VampirismModuleExtensions.ModuleId);
        var damage = Math.Max(0m, effect.Amount + (effect.ScalingAttributeId is { } attributeId
            ? context.Attributes.GetValueOrDefault(attributeId) * effect.ScalingFactor
            : 0m));
        return new(
            MonsterDamage: damage,
            ResourceChanges: new Dictionary<string, decimal>
            {
                [options.HealingResourceId] = damage * options.HealingRatio
            });
    }
}

/// <summary>校验吸血模块配置的数值范围以及对核心资源的跨内容引用。</summary>
public sealed class VampirismContentValidator : IGameContentValidator
{
    public IEnumerable<ContentValidationIssue> Validate(GameContentVersion content)
    {
        if (!content.Content.Effects.Values.Any(effect => BuiltInEffectTypes.Resolve(effect) == VampiricDamageEffect.EffectTypeId)) yield break;
        var options = content.GetModuleContent<VampirismContent>(VampirismModuleExtensions.ModuleId);
        if (options.HealingRatio is < 0m or > 1m)
            yield return new("$.modules.sample.vampirism.healingRatio", "invalid_healing_ratio", "Healing ratio must be between 0 and 1.", VampirismModuleExtensions.ModuleId);
        if (string.IsNullOrWhiteSpace(options.HealingResourceId) || !content.Content.Resources.ContainsKey(options.HealingResourceId))
            yield return new("$.modules.sample.vampirism.healingResourceId", "unknown_resource", $"Healing resource '{options.HealingResourceId}' is not defined.", VampirismModuleExtensions.ModuleId);
    }
}

/// <summary>向任意宿主显式注册示例吸血模块，不依赖 Gateway、Grain 或 Infrastructure 实现。</summary>
public static class VampirismModuleExtensions
{
    public const string ModuleId = "sample.vampirism";

    public static GameServerBuilder AddSampleVampirism(this GameServerBuilder builder)
        => builder.AddModule(
            new GameModuleDescriptor(ModuleId, ["core.gameplay"]),
            module => module
                .AddEffect<VampiricDamageEffect>()
                .AddContentSection(required: false, defaultFactory: () => new VampirismContent())
                .AddContentValidator<VampirismContentValidator>());
}
