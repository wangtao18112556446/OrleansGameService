using GameServer.Contracts;

namespace GameServer.Domain.Attributes;

/// <summary>Deep module for applying base values, flat modifiers, percentage modifiers, and configured limits.</summary>
public sealed class AttributeSet
{
    private readonly IReadOnlyDictionary<string, AttributeDefinition> _definitions;
    private readonly Dictionary<string, decimal> _baseValues;
    private readonly List<AttributeModifier> _modifiers = [];

    public AttributeSet(IReadOnlyDictionary<string, AttributeDefinition> definitions, IDictionary<string, decimal>? baseValues = null)
    {
        _definitions = definitions;
        _baseValues = baseValues is null ? [] : new Dictionary<string, decimal>(baseValues, StringComparer.Ordinal);
    }

    public decimal Get(string attributeId)
    {
        if (!_definitions.TryGetValue(attributeId, out var definition))
            throw new InvalidOperationException($"Unknown attribute '{attributeId}'.");

        var baseValue = _baseValues.GetValueOrDefault(attributeId, definition.DefaultValue);
        var modifiers = _modifiers.Where(x => x.AttributeId == attributeId).ToArray();
        var value = (baseValue + modifiers.Sum(x => x.FlatAmount)) * (1m + modifiers.Sum(x => x.PercentAmount));
        if (definition.Minimum is { } minimum) value = Math.Max(value, minimum);
        if (definition.Maximum is { } maximum) value = Math.Min(value, maximum);
        return decimal.Round(value, 2, MidpointRounding.AwayFromZero);
    }

    public IReadOnlyDictionary<string, decimal> Snapshot()
        => _definitions.Keys.ToDictionary(x => x, Get, StringComparer.Ordinal);

    public void SetBase(string attributeId, decimal value)
    {
        EnsureKnown(attributeId);
        _baseValues[attributeId] = value;
    }

    public void AddModifier(AttributeModifier modifier)
    {
        EnsureKnown(modifier.AttributeId);
        _modifiers.Add(modifier);
    }

    public void RemoveSource(string source) => _modifiers.RemoveAll(x => x.Source == source);

    private void EnsureKnown(string attributeId)
    {
        if (!_definitions.ContainsKey(attributeId))
            throw new InvalidOperationException($"Unknown attribute '{attributeId}'.");
    }
}
