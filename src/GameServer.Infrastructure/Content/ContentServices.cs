using GameServer.Abstractions;
using GameServer.Contracts;
using GameServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace GameServer.Infrastructure.Content;

/// <summary>线程安全地保存已编译内容版本，并以单次赋值切换进程内活动版本。</summary>
public sealed class JsonGameContentCatalog : IGameContentCatalog
{
    private readonly Lock sync = new();
    private readonly Dictionary<string, GameContentVersion> versions = new(StringComparer.Ordinal);
    private GameContentVersion active;

    public JsonGameContentCatalog(
        IHostEnvironment environment,
        IOptions<GameContentOptions> options,
        JsonGameContentCompiler compiler)
    {
        var configuredPath = options.Value.BootstrapPath;
        var path = Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.Combine(environment.ContentRootPath, configuredPath);
        if (File.Exists(path)) active = compiler.Compile(File.ReadAllText(path));
        else if (options.Value.AllowBuiltInFallback) active = compiler.Compile(GameContentDefaults.Create());
        else throw new FileNotFoundException($"Bootstrap content file '{path}' does not exist.", path);
        versions.Add(active.Version, active);
    }

    public GameContentVersion GetVersion(string version)
    {
        lock (sync)
            return versions.TryGetValue(version, out var content)
                ? content
                : throw new InvalidOperationException($"Content version '{version}' is not available.");
    }

    public GameContentVersion GetActive()
    {
        lock (sync) return active;
    }

    public IReadOnlyList<GameContentVersion> GetAll()
    {
        lock (sync) return [.. versions.Values];
    }

    public void Load(GameContentVersion content, bool activate)
    {
        lock (sync)
        {
            if (versions.TryGetValue(content.Version, out var existing)
                && !string.Equals(existing.SourceHash, content.SourceHash, StringComparison.Ordinal))
                throw new InvalidOperationException($"Content version '{content.Version}' is immutable and already has different content.");
            versions[content.Version] = content;
            if (activate) active = content;
        }
    }
}

/// <summary>在数据库提交不可变内容发布后，才切换当前进程的活动内容版本。</summary>
public sealed class ContentImportService(
    IDbContextFactory<GameDbContext> database,
    IGameContentCatalog catalog,
    JsonGameContentCompiler compiler)
{
    public async Task<GameContent> ImportAsync(string contentJson, string importedBy, CancellationToken cancellationToken = default)
    {
        var version = compiler.Compile(contentJson);
        await using var db = await database.CreateDbContextAsync(cancellationToken);
        if (await db.ContentReleases.AnyAsync(x => x.Version == version.Version, cancellationToken))
            throw new InvalidOperationException($"Content version '{version.Version}' is immutable and already exists.");
        db.ContentReleases.Add(new ContentReleaseEntity
        {
            Version = version.Version,
            ContentJson = contentJson,
            ImportedBy = importedBy,
            ImportedAt = DateTimeOffset.UtcNow
        });
        var active = await db.ContentSettings.SingleOrDefaultAsync(cancellationToken);
        if (active is null) db.ContentSettings.Add(new ContentSettingsEntity { Id = 1, ActiveVersion = version.Version });
        else active.ActiveVersion = version.Version;
        await db.SaveChangesAsync(cancellationToken);
        catalog.Load(version, activate: true);
        return version.Content;
    }
}

/// <summary>启动时重编译数据库中的历史内容，并恢复持久化的活动版本指针。</summary>
public sealed class ContentCatalogLoader(
    IDbContextFactory<GameDbContext> database,
    IGameContentCatalog catalog,
    JsonGameContentCompiler compiler) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var db = await database.CreateDbContextAsync(cancellationToken);
        var releases = await db.ContentReleases.AsNoTracking().ToListAsync(cancellationToken);
        foreach (var release in releases)
        {
            var content = compiler.Compile(release.ContentJson);
            try { catalog.Load(content, activate: false); }
            catch (InvalidOperationException) when (catalog.GetVersion(content.Version).SourceHash == content.SourceHash) { }
        }
        var activeVersion = await db.ContentSettings.AsNoTracking().Select(x => x.ActiveVersion).SingleOrDefaultAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(activeVersion)) catalog.Load(catalog.GetVersion(activeVersion), activate: true);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>提供测试和允许回退时可运行的最小核心玩法内容。</summary>
public static class GameContentDefaults
{
    public static GameContent Create() => new("v1",
        new Dictionary<string, AttributeDefinition>(StringComparer.Ordinal)
        {
            ["health"] = new("health", 100, 1),
            ["attack"] = new("attack", 25, 0),
            ["attackRange"] = new("attackRange", 3, 1, 8)
        },
        new Dictionary<string, ItemDefinition>(StringComparer.Ordinal)
        {
            ["slime-gel"] = new("slime-gel", "Slime Gel"),
            ["traveler-token"] = new("traveler-token", "Traveler Token")
        },
        new Dictionary<string, MonsterDefinition>(StringComparer.Ordinal)
        {
            ["green-slime"] = new("green-slime", "Green Slime", 50, 8, new WorldPosition(4, 0), "slime-gel", 1)
        },
        new Dictionary<string, QuestDefinition>(StringComparer.Ordinal)
        {
            ["slime-hunt"] = new("slime-hunt", "guard-aria", "green-slime", 1, "traveler-token", 1)
        })
    {
        Classes = new Dictionary<string, CharacterClassDefinition> { ["adventurer"] = new("adventurer", "Adventurer", new Dictionary<string, decimal>()) { InitialSkillIds = ["basic-attack"] } },
        Resources = new Dictionary<string, ResourceDefinition> { ["health"] = new("health", "Health", "health") { InitialValue = 100 } },
        Skills = new Dictionary<string, SkillDefinition> { ["basic-attack"] = new("basic-attack", "Basic Attack", 0, TimeSpan.FromMilliseconds(500), "physical-damage") },
        Effects = new Dictionary<string, EffectDefinition> { ["physical-damage"] = new("physical-damage", EffectKind.DamageMonster) { ScalingAttributeId = "attack", ScalingFactor = 1 } },
        Zones = new Dictionary<string, ZoneDefinition> { ["starter-plains"] = new("starter-plains", "Starter Plains", 100) },
        Npcs = new Dictionary<string, NpcDefinition> { ["guard-aria"] = new("guard-aria", "Guard Aria", new WorldPosition(0, 2), ["slime-hunt"]) },
        Movement = new MovementDefinition(6f, TimeSpan.FromSeconds(2))
    };
}
