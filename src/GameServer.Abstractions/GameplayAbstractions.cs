using GameServer.Contracts;

namespace GameServer.Abstractions;

/// <summary>定义内置效果的稳定字符串标识，并兼容旧内容中的 EffectKind。</summary>
public static class BuiltInEffectTypes
{
    public const string DamageMonster = "core.damage-monster";
    public const string RestoreResource = "core.restore-resource";
    public const string ConsumeResource = "core.consume-resource";
    public const string ApplyBuff = "core.apply-buff";

    public static string Resolve(EffectDefinition effect)
        => effect.TypeId is null
            ? effect.Kind switch
            {
                EffectKind.DamageMonster => DamageMonster,
                EffectKind.RestoreResource => RestoreResource,
                EffectKind.ConsumeResource => ConsumeResource,
                EffectKind.ApplyBuff => ApplyBuff,
                _ => throw new InvalidOperationException($"Effect kind '{effect.Kind}' has no compatible type id.")
            }
            : !string.IsNullOrWhiteSpace(effect.TypeId)
                ? effect.TypeId
                : throw new InvalidOperationException($"Effect '{effect.Id}' has an empty type id.");
}

/// <summary>携带效果计算所需的显式、确定性输入，包括当前不可变内容版本。</summary>
public sealed record EffectContext(
    GameContentVersion ContentVersion,
    IReadOnlyDictionary<string, decimal> Attributes,
    string? TargetMonsterId = null);

/// <summary>以纯数据描述一次效果解析产生的伤害、资源变化和 Buff。</summary>
public sealed record EffectResolution(
    decimal MonsterDamage = 0m,
    IReadOnlyDictionary<string, decimal>? ResourceChanges = null,
    string? BuffId = null)
{
    public IReadOnlyDictionary<string, decimal> ResourceChangesOrEmpty
        => ResourceChanges ?? new Dictionary<string, decimal>();
}

/// <summary>封装一种效果类型的内容约束和确定性结算逻辑。</summary>
public interface IGameEffect
{
    string TypeId { get; }
    IEnumerable<ContentValidationIssue> Validate(EffectDefinition effect, GameContentVersion content);
    EffectResolution Resolve(EffectDefinition effect, EffectContext context);
}

/// <summary>按稳定类型标识查询冻结后的效果实现，禁止运行期修改注册表。</summary>
public interface IGameplayFeatureCatalog
{
    IGameEffect GetEffect(string typeId);
    bool Supports(string typeId);
}
