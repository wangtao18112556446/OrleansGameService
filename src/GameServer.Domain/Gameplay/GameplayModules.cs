using GameServer.Contracts;

namespace GameServer.Domain.Gameplay;

public interface IGameFeatureModule { string Id { get; } void Configure(GameFeatureRegistry registry); }
public interface IGameRuleHandler<in TCommand> { RuleDecision Evaluate(TCommand command); }
public interface IGameEffect { EffectKind Kind { get; } EffectResolution Resolve(EffectDefinition effect, EffectContext context); }
public interface IAttributeFormula { string AttributeId { get; } decimal Evaluate(IReadOnlyDictionary<string, decimal> attributes); }
public interface IConditionEvaluator { bool Matches(string condition, IReadOnlyDictionary<string, decimal> attributes); }

public sealed class GameFeatureRegistry
{
    private readonly Dictionary<EffectKind, IGameEffect> effects = [];
    public void AddEffect(IGameEffect effect)
    {
        if (!effects.TryAdd(effect.Kind, effect)) throw new InvalidOperationException($"Effect kind '{effect.Kind}' is already registered.");
    }
    public IGameEffect GetEffect(EffectKind kind) => effects.TryGetValue(kind, out var effect) ? effect : throw new InvalidOperationException($"Effect kind '{kind}' is not registered.");
    public bool Supports(EffectKind kind) => effects.ContainsKey(kind);
}

public sealed record RuleDecision(bool Allowed, string? ErrorCode = null);
public sealed record EffectContext(IReadOnlyDictionary<string, decimal> Attributes, string? TargetMonsterId = null);
public sealed record EffectResolution(decimal MonsterDamage = 0m, IReadOnlyDictionary<string, decimal>? ResourceChanges = null, string? BuffId = null)
{
    public IReadOnlyDictionary<string, decimal> ResourceChangesOrEmpty => ResourceChanges ?? new Dictionary<string, decimal>();
}

public sealed class DamageMonsterEffect : IGameEffect
{
    public EffectKind Kind => EffectKind.DamageMonster;
    public EffectResolution Resolve(EffectDefinition effect, EffectContext context) => new(MonsterDamage: EffectValue.Resolve(effect, context.Attributes));
}
public sealed class ResourceEffect : IGameEffect
{
    public ResourceEffect(EffectKind kind) => Kind = kind;
    public EffectKind Kind { get; }
    public EffectResolution Resolve(EffectDefinition effect, EffectContext context)
    {
        var amount = EffectValue.Resolve(effect, context.Attributes);
        if (Kind == EffectKind.ConsumeResource) amount = -amount;
        return new(ResourceChanges: new Dictionary<string, decimal> { [effect.ResourceId ?? throw new InvalidOperationException("Resource effect requires a resource id.")] = amount });
    }
}
public sealed class ApplyBuffEffect : IGameEffect
{
    public EffectKind Kind => EffectKind.ApplyBuff;
    public EffectResolution Resolve(EffectDefinition effect, EffectContext context) => new(BuffId: effect.BuffId ?? throw new InvalidOperationException("Buff effect requires a buff id."));
}
public static class EffectValue
{
    public static decimal Resolve(EffectDefinition effect, IReadOnlyDictionary<string, decimal> attributes)
        => Math.Max(0m, effect.Amount + (effect.ScalingAttributeId is { } id ? attributes.GetValueOrDefault(id) * effect.ScalingFactor : 0m));
}
public sealed class CoreGameplayModule : IGameFeatureModule
{
    public string Id => "core-gameplay";
    public void Configure(GameFeatureRegistry registry)
    {
        registry.AddEffect(new DamageMonsterEffect());
        registry.AddEffect(new ResourceEffect(EffectKind.RestoreResource));
        registry.AddEffect(new ResourceEffect(EffectKind.ConsumeResource));
        registry.AddEffect(new ApplyBuffEffect());
    }
}
public static class GameplayCore
{
    public static GameFeatureRegistry CreateRegistry() { var registry = new GameFeatureRegistry(); new CoreGameplayModule().Configure(registry); return registry; }
}
