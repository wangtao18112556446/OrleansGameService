using GameServer.Contracts;

namespace GameServer.Abstractions;

/// <summary>保存已校验的核心内容及各模块的强类型内容，并作为运行期不可变内容快照。</summary>
public sealed class GameContentVersion
{
    private readonly IReadOnlyDictionary<string, object> moduleContent;

    public GameContentVersion(
        GameContent content,
        IReadOnlyDictionary<string, object>? moduleContent = null,
        string? sourceHash = null)
    {
        Content = content;
        this.moduleContent = new Dictionary<string, object>(moduleContent ?? new Dictionary<string, object>(), StringComparer.Ordinal);
        SourceHash = sourceHash;
    }

    public GameContent Content { get; }
    public string Version => Content.Version;
    public string? SourceHash { get; }
    public IReadOnlyCollection<string> ModuleIds => [.. moduleContent.Keys];

    public T GetModuleContent<T>(string moduleId) where T : class
        => moduleContent.TryGetValue(moduleId, out var value) && value is T typed
            ? typed
            : throw new InvalidOperationException($"Module content '{moduleId}' is unavailable or is not {typeof(T).Name}.");

    public bool TryGetModuleContent<T>(string moduleId, out T? content) where T : class
    {
        content = moduleContent.GetValueOrDefault(moduleId) as T;
        return content is not null;
    }
}

/// <summary>提供进程内内容版本的读取与原子激活接缝，隐藏目录同步和并发实现。</summary>
public interface IGameContentCatalog
{
    GameContentVersion GetVersion(string version);
    GameContentVersion GetActive();
    IReadOnlyList<GameContentVersion> GetAll();
    void Load(GameContentVersion content, bool activate);
}

/// <summary>描述可定位到模块和内容路径的校验问题，供导入接口一次返回完整诊断。</summary>
public sealed record ContentValidationIssue(string Path, string Code, string Message, string? ModuleId = null);

/// <summary>聚合内容校验问题并阻止无效版本进入活动目录。</summary>
public sealed class ContentValidationException(IReadOnlyList<ContentValidationIssue> issues)
    : InvalidOperationException(string.Join(Environment.NewLine, issues.Select(issue => $"{issue.Path}: {issue.Message}")))
{
    public IReadOnlyList<ContentValidationIssue> Issues { get; } = [.. issues];
}

/// <summary>允许模块校验已绑定的完整内容版本，包括自身配置与跨内容引用。</summary>
public interface IGameContentValidator
{
    IEnumerable<ContentValidationIssue> Validate(GameContentVersion content);
}

/// <summary>以角色与操作号去重记录权威奖励投递，隔离外部账本实现。</summary>
public interface IRewardLedger
{
    Task<bool> TryRecordAsync(
        string characterId,
        string operationId,
        string kind,
        IReadOnlyList<RewardGrant> rewards,
        CancellationToken cancellationToken = default);
}
