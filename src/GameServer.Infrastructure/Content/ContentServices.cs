using System.Text.Json;
using System.Text.Json.Serialization;
using GameServer.Contracts;
using GameServer.Domain.Gameplay;
using GameServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;

namespace GameServer.Infrastructure.Content;

/// <summary>加载、校验并按版本保存进程内不可变游戏内容。</summary>
public sealed class JsonGameContentCatalog : IGameContentCatalog
{
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();
    private readonly Lock sync = new();
    private readonly Dictionary<string, GameContent> versions = new(StringComparer.Ordinal);
    private GameContent active;
    public JsonGameContentCatalog(IHostEnvironment environment)
    {
        var path = Path.Combine(environment.ContentRootPath, "Content", "game-content.v1.json");
        active = File.Exists(path) ? Deserialize(File.ReadAllText(path)) : GameContentDefaults.Create();
        Validate(active);
        versions.Add(active.Version, active);
    }
    public GameContent GetVersion(string version) { lock (sync) return versions.TryGetValue(version, out var content) ? content : throw new InvalidOperationException($"Content version '{version}' is not available."); }
    public GameContent GetActive() { lock (sync) return active; }
    public IReadOnlyList<GameContent> GetAll() 
    { 
        lock (sync) 
        {
            return [.. versions.Values];
        }
    }
    public void Activate(GameContent content)
    {
        Validate(content);
        lock (sync) { versions[content.Version] = content; active = content; }
    }
    public void AddVersion(GameContent content)
    {
        Validate(content);
        lock (sync) { if (!versions.TryAdd(content.Version, content) && versions[content.Version] != content) throw new InvalidOperationException($"Content version '{content.Version}' already exists."); }
    }
    public static GameContent Deserialize(string json) => JsonSerializer.Deserialize<GameContent>(json, SerializerOptions) ?? throw new InvalidOperationException("Content package is empty or invalid.");
    public static void Validate(GameContent content, GameFeatureRegistry? features = null)
    {
        if (string.IsNullOrWhiteSpace(content.Version)) throw new InvalidOperationException("Content version is required.");
        _ = MovementRules.Capacity(content.Movement ?? throw new InvalidOperationException("Movement definition is required."));
        features ??= GameplayCore.CreateRegistry();
        foreach (var item in content.Items.Values) if (item.MaxStack <= 0) throw new InvalidOperationException($"Item '{item.Id}' must have a positive max stack.");
        foreach (var characterClass in content.Classes.Values)
        {
            foreach (var attribute in characterClass.InitialAttributes.Keys) Ensure(content.Attributes.ContainsKey(attribute), $"Class '{characterClass.Id}' references unknown attribute '{attribute}'.");
            foreach (var skill in characterClass.InitialSkillIds) Ensure(content.Skills.ContainsKey(skill), $"Class '{characterClass.Id}' references unknown skill '{skill}'.");
            foreach (var item in characterClass.InitialItems) Ensure(content.Items.ContainsKey(item.ItemId) && item.Quantity > 0, $"Class '{characterClass.Id}' has an invalid initial item.");
        }
        foreach (var resource in content.Resources.Values) Ensure(content.Attributes.ContainsKey(resource.MaximumAttributeId), $"Resource '{resource.Id}' references unknown maximum attribute.");
        foreach (var slot in content.EquipmentSlots.Values) Ensure(slot.AcceptedItemTags.Count > 0, $"Equipment slot '{slot.Id}' must accept a tag.");
        foreach (var item in content.Items.Values) foreach (var modifier in item.Modifiers) Ensure(content.Attributes.ContainsKey(modifier.AttributeId), $"Item '{item.Id}' references unknown attribute.");
        foreach (var buff in content.Buffs.Values) foreach (var modifier in buff.Modifiers) Ensure(content.Attributes.ContainsKey(modifier.AttributeId), $"Buff '{buff.Id}' references unknown attribute.");
        foreach (var skill in content.Skills.Values)
        {
            if (skill.ResourceId is { } resourceId) Ensure(content.Resources.ContainsKey(resourceId), $"Skill '{skill.Id}' references unknown resource.");
            foreach (var effectId in skill.EffectIds.Count == 0 ? [skill.EffectId] : skill.EffectIds) Ensure(content.Effects.ContainsKey(effectId), $"Skill '{skill.Id}' references unknown effect '{effectId}'.");
        }
        foreach (var effect in content.Effects.Values)
        {
            Ensure(features.Supports(effect.Kind), $"Effect '{effect.Id}' has an unsupported kind.");
            if (effect.Kind is EffectKind.RestoreResource or EffectKind.ConsumeResource) Ensure(!string.IsNullOrWhiteSpace(effect.ResourceId), $"Effect '{effect.Id}' requires a resource.");
            if (effect.Kind == EffectKind.ApplyBuff) Ensure(!string.IsNullOrWhiteSpace(effect.BuffId), $"Effect '{effect.Id}' requires a buff.");
            if (effect.ResourceId is { } resourceId) Ensure(content.Resources.ContainsKey(resourceId), $"Effect '{effect.Id}' references unknown resource.");
            if (effect.BuffId is { } buffId) Ensure(content.Buffs.ContainsKey(buffId), $"Effect '{effect.Id}' references unknown buff.");
            if (effect.ScalingAttributeId is { } attributeId) Ensure(content.Attributes.ContainsKey(attributeId), $"Effect '{effect.Id}' references unknown scaling attribute.");
        }
        foreach (var table in content.DropTables.Values) foreach (var entry in table.Entries) Ensure(content.Items.ContainsKey(entry.ItemId) && entry.Quantity > 0 && entry.Probability is >= 0m and <= 1m, $"Drop table '{table.Id}' has an invalid entry.");
        foreach (var monster in content.Monsters.Values)
        {
            if (monster.DropTableId is { } tableId) Ensure(content.DropTables.ContainsKey(tableId), $"Monster '{monster.Id}' references unknown drop table.");
            else Ensure(content.Items.ContainsKey(monster.DropItemId) && monster.DropCount > 0, $"Monster '{monster.Id}' references unknown item.");
        }
        foreach (var npc in content.Npcs.Values)
        {
            Ensure(content.Zones.ContainsKey(npc.ZoneId), $"NPC '{npc.Id}' references unknown zone.");
            foreach (var questId in npc.QuestIds) Ensure(content.Quests.ContainsKey(questId), $"NPC '{npc.Id}' references unknown quest.");
        }
        foreach (var quest in content.Quests.Values) { Ensure(content.Npcs.ContainsKey(quest.NpcId), $"Quest '{quest.Id}' references unknown NPC."); Ensure(content.Monsters.ContainsKey(quest.TargetMonsterId), $"Quest '{quest.Id}' references unknown monster."); Ensure(content.Items.ContainsKey(quest.RewardItemId), $"Quest '{quest.Id}' references unknown item."); }
    }
    private static void Ensure(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new ReadOnlyStringSetJsonConverter());
        return options;
    }
}

/// <summary>把内容包中的字符串数组适配为只读集合，同时保留区分大小写的内容标识语义。</summary>
internal sealed class ReadOnlyStringSetJsonConverter : JsonConverter<IReadOnlySet<string>>
{
    public override IReadOnlySet<string> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => new HashSet<string>(JsonSerializer.Deserialize<string[]>(ref reader, options) ?? [], StringComparer.Ordinal);

    public override void Write(Utf8JsonWriter writer, IReadOnlySet<string> value, JsonSerializerOptions options)
        => JsonSerializer.Serialize(writer, value.ToArray(), options);
}

public sealed class ContentImportService(IDbContextFactory<GameDbContext> database, IGameContentCatalog catalog, GameFeatureRegistry features)
{
    public async Task<GameContent> ImportAsync(string contentJson, string importedBy, CancellationToken cancellationToken = default)
    {
        var content = JsonGameContentCatalog.Deserialize(contentJson);
        JsonGameContentCatalog.Validate(content, features);
        await using var db = await database.CreateDbContextAsync(cancellationToken);
        if (await db.ContentReleases.AnyAsync(x => x.Version == content.Version, cancellationToken)) throw new InvalidOperationException($"Content version '{content.Version}' is immutable and already exists.");
        db.ContentReleases.Add(new ContentReleaseEntity { Version = content.Version, ContentJson = contentJson, ImportedBy = importedBy, ImportedAt = DateTimeOffset.UtcNow });
        var active = await db.ContentSettings.SingleOrDefaultAsync(cancellationToken);
        if (active is null) db.ContentSettings.Add(new ContentSettingsEntity { Id = 1, ActiveVersion = content.Version }); else active.ActiveVersion = content.Version;
        await db.SaveChangesAsync(cancellationToken);
        catalog.Activate(content);
        return content;
    }
}

public sealed class ContentCatalogLoader(IDbContextFactory<GameDbContext> database, IGameContentCatalog catalog) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var db = await database.CreateDbContextAsync(cancellationToken);
        var releases = await db.ContentReleases.AsNoTracking().ToListAsync(cancellationToken);
        foreach (var release in releases)
        {
            var content = JsonGameContentCatalog.Deserialize(release.ContentJson);
            if (catalog is JsonGameContentCatalog json) { try { json.AddVersion(content); } catch (InvalidOperationException) { } }
        }
        var activeVersion = await db.ContentSettings.AsNoTracking().Select(x => x.ActiveVersion).SingleOrDefaultAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(activeVersion)) catalog.Activate(catalog.GetVersion(activeVersion));
    }
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>提供测试和内容文件缺失时可运行的最小玩法内容。</summary>
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
