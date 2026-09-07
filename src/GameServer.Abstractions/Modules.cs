using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace GameServer.Abstractions;

/// <summary>声明编译期模块的稳定标识及其必须先存在的模块依赖。</summary>
public sealed record GameModuleDescriptor(string Id, IReadOnlyCollection<string>? Dependencies = null)
{
    public IReadOnlyCollection<string> RequiredModules { get; } = [.. Dependencies ?? []];
}

/// <summary>记录效果实现的归属，供启动期构建不可变玩法目录并诊断冲突。</summary>
public sealed record GameEffectRegistration(string ModuleId, Type ImplementationType);

/// <summary>记录一个模块内容节的类型、必需性和向后兼容默认值。</summary>
public sealed record GameContentSectionRegistration(
    string ModuleId,
    Type ContentType,
    bool Required,
    Func<object>? DefaultFactory);

/// <summary>收集显式模块贡献并在首次读取时校验、排序和冻结模块图。</summary>
public sealed class GameModuleCollection
{
    private readonly Dictionary<string, GameModuleDescriptor> modules = new(StringComparer.Ordinal);
    private readonly List<GameEffectRegistration> effects = [];
    private readonly Dictionary<string, GameContentSectionRegistration> contentSections = new(StringComparer.Ordinal);
    private IReadOnlyList<GameModuleDescriptor>? orderedModules;

    public IReadOnlyList<GameEffectRegistration> Effects => [.. effects];
    public IReadOnlyList<GameContentSectionRegistration> ContentSections => [.. contentSections.Values];

    public void AddModule(GameModuleDescriptor descriptor)
    {
        EnsureMutable();
        if (string.IsNullOrWhiteSpace(descriptor.Id)) throw new InvalidOperationException("Module id is required.");
        if (!modules.TryAdd(descriptor.Id, descriptor)) throw new InvalidOperationException($"Module '{descriptor.Id}' is already registered.");
    }

    public void AddEffect(string moduleId, Type implementationType)
    {
        EnsureMutable();
        effects.Add(new GameEffectRegistration(moduleId, implementationType));
    }

    public void AddContentSection(GameContentSectionRegistration registration)
    {
        EnsureMutable();
        if (!contentSections.TryAdd(registration.ModuleId, registration))
            throw new InvalidOperationException($"Module '{registration.ModuleId}' already registered a content section.");
    }

    public IReadOnlyList<GameModuleDescriptor> Freeze()
    {
        if (orderedModules is not null) return orderedModules;
        var state = new Dictionary<string, int>(StringComparer.Ordinal);
        var result = new List<GameModuleDescriptor>(modules.Count);

        foreach (var module in modules.Values.OrderBy(module => module.Id, StringComparer.Ordinal)) Visit(module);
        orderedModules = result;
        return orderedModules;

        void Visit(GameModuleDescriptor module)
        {
            var current = state.GetValueOrDefault(module.Id);
            if (current == 2) return;
            if (current == 1) throw new InvalidOperationException($"Module dependency cycle contains '{module.Id}'.");
            state[module.Id] = 1;
            foreach (var dependencyId in module.RequiredModules.Order(StringComparer.Ordinal))
            {
                if (!modules.TryGetValue(dependencyId, out var dependency))
                    throw new InvalidOperationException($"Module '{module.Id}' requires missing module '{dependencyId}'.");
                Visit(dependency);
            }
            state[module.Id] = 2;
            result.Add(module);
        }
    }

    private void EnsureMutable()
    {
        if (orderedModules is not null) throw new InvalidOperationException("The module graph is already frozen.");
    }
}

/// <summary>向二次开发包提供单一组合入口，并保留标准依赖注入和配置能力。</summary>
public sealed class GameServerBuilder
{
    private readonly GameModuleCollection modules;

    public GameServerBuilder(IServiceCollection services, IConfiguration configuration, GameModuleCollection modules)
    {
        Services = services;
        Configuration = configuration;
        this.modules = modules;
    }

    public IServiceCollection Services { get; }
    public IConfiguration Configuration { get; }

    public GameServerBuilder AddModule(GameModuleDescriptor descriptor, Action<GameModuleBuilder> configure)
    {
        modules.AddModule(descriptor);
        configure(new GameModuleBuilder(descriptor.Id, Services, modules));
        return this;
    }
}

/// <summary>限制单个模块可贡献的扩展类型，同时允许其注册内部实现依赖。</summary>
public sealed class GameModuleBuilder
{
    private readonly string moduleId;
    private readonly GameModuleCollection modules;

    public GameModuleBuilder(string moduleId, IServiceCollection services, GameModuleCollection modules)
    {
        this.moduleId = moduleId;
        Services = services;
        this.modules = modules;
    }

    public IServiceCollection Services { get; }

    public GameModuleBuilder AddEffect<TEffect>() where TEffect : class, IGameEffect
    {
        Services.AddSingleton<TEffect>();
        modules.AddEffect(moduleId, typeof(TEffect));
        return this;
    }

    public GameModuleBuilder AddContentSection<TContent>(bool required = false, Func<TContent>? defaultFactory = null)
        where TContent : class
    {
        modules.AddContentSection(new GameContentSectionRegistration(
            moduleId,
            typeof(TContent),
            required,
            defaultFactory is null ? null : () => defaultFactory()));
        return this;
    }

    public GameModuleBuilder AddContentValidator<TValidator>() where TValidator : class, IGameContentValidator
    {
        Services.AddSingleton<TValidator>();
        Services.AddSingleton<IGameContentValidator>(provider => provider.GetRequiredService<TValidator>());
        return this;
    }
}
