using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using GameServer.Abstractions;
using GameServer.Contracts;

namespace GameServer.Infrastructure.Content;

/// <summary>把 JSON 内容包编译为已绑定、已聚合校验且可按版本复用的运行期内容。</summary>
public sealed class JsonGameContentCompiler(
    GameModuleCollection modules,
    IEnumerable<IGameContentValidator> validators)
{
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    public GameContentVersion Compile(string json)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(json); }
        catch (JsonException exception)
        {
            throw new ContentValidationException([new(exception.Path ?? "$", "invalid_json", exception.Message)]);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new ContentValidationException([new("$", "invalid_document", "Content package must be a JSON object.")]);

            GameContent core;
            try
            {
                core = document.RootElement.Deserialize<GameContent>(SerializerOptions)
                    ?? throw new JsonException("Content package is empty.");
            }
            catch (JsonException exception)
            {
                throw new ContentValidationException([new(exception.Path ?? "$", "invalid_core_content", exception.Message)]);
            }

            var issues = new List<ContentValidationIssue>();
            var boundSections = BindModuleSections(document.RootElement, issues);
            if (issues.Count > 0) throw new ContentValidationException(issues);

            var normalized = JsonSerializer.Serialize(document.RootElement, SerializerOptions);
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
            var version = new GameContentVersion(core, boundSections, hash);
            foreach (var validator in validators)
                issues.AddRange(validator.Validate(version));
            if (issues.Count > 0) throw new ContentValidationException(issues);
            return version;
        }
    }

    public GameContentVersion Compile(GameContent content)
        => Compile(JsonSerializer.Serialize(content, SerializerOptions));

    private IReadOnlyDictionary<string, object> BindModuleSections(JsonElement root, List<ContentValidationIssue> issues)
    {
        var registrations = modules.ContentSections.ToDictionary(section => section.ModuleId, StringComparer.Ordinal);
        var supplied = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (root.TryGetProperty("modules", out var moduleRoot))
        {
            if (moduleRoot.ValueKind != JsonValueKind.Object)
            {
                issues.Add(new("$.modules", "invalid_module_sections", "The modules property must be a JSON object."));
                return new Dictionary<string, object>();
            }
            foreach (var property in moduleRoot.EnumerateObject())
            {
                supplied[property.Name] = property.Value;
                if (!registrations.ContainsKey(property.Name))
                    issues.Add(new($"$.modules.{property.Name}", "unknown_module", $"Content references unregistered module '{property.Name}'.", property.Name));
            }
        }

        var result = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var registration in modules.Freeze()
                     .Select(module => registrations.GetValueOrDefault(module.Id))
                     .Where(registration => registration is not null))
        {
            var section = registration!;
            if (!supplied.TryGetValue(section.ModuleId, out var json))
            {
                if (section.DefaultFactory is not null) result[section.ModuleId] = section.DefaultFactory();
                else if (section.Required) issues.Add(new($"$.modules.{section.ModuleId}", "missing_module_content", $"Module '{section.ModuleId}' requires a content section.", section.ModuleId));
                continue;
            }
            try
            {
                var value = json.Deserialize(section.ContentType, SerializerOptions);
                if (value is null) throw new JsonException("Module content cannot be null.");
                result[section.ModuleId] = value;
            }
            catch (JsonException exception)
            {
                var suffix = string.IsNullOrWhiteSpace(exception.Path) || exception.Path == "$" ? string.Empty : exception.Path[1..];
                issues.Add(new($"$.modules.{section.ModuleId}{suffix}", "invalid_module_content", exception.Message, section.ModuleId));
            }
        }
        return result;
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new ReadOnlyStringSetJsonConverter());
        return options;
    }
}

/// <summary>把内容包中的字符串数组适配为区分大小写的只读集合。</summary>
internal sealed class ReadOnlyStringSetJsonConverter : JsonConverter<IReadOnlySet<string>>
{
    public override IReadOnlySet<string> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => new HashSet<string>(JsonSerializer.Deserialize<string[]>(ref reader, options) ?? [], StringComparer.Ordinal);

    public override void Write(Utf8JsonWriter writer, IReadOnlySet<string> value, JsonSerializerOptions options)
        => JsonSerializer.Serialize(writer, value.ToArray(), options);
}
