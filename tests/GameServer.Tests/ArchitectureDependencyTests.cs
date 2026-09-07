using System.Xml.Linq;

namespace GameServer.Tests;

/// <summary>验证二次开发抽象及示例模块没有反向依赖宿主或基础设施实现。</summary>
public sealed class ArchitectureDependencyTests
{
    [Fact]
    public void Abstractions_and_sample_module_keep_their_reference_contracts()
    {
        var root = RepositoryRoot();
        Assert.Equal(
            ["../GameServer.Contracts/GameServer.Contracts.csproj"],
            ProjectReferences(Path.Combine(root, "src", "GameServer.Abstractions", "GameServer.Abstractions.csproj")));
        Assert.Equal(
            ["../../src/GameServer.Abstractions/GameServer.Abstractions.csproj"],
            ProjectReferences(Path.Combine(root, "samples", "GameServer.SampleGameplay", "GameServer.SampleGameplay.csproj")));
        Assert.DoesNotContain(ProjectReferences(Path.Combine(root, "src", "GameServer.Grains", "GameServer.Grains.csproj")), reference => reference.Contains("Infrastructure", StringComparison.Ordinal));
        Assert.DoesNotContain(ProjectReferences(Path.Combine(root, "src", "GameServer.Domain", "GameServer.Domain.csproj")), reference => reference.Contains("Infrastructure", StringComparison.Ordinal) || reference.Contains("Gateway", StringComparison.Ordinal));
    }

    private static IReadOnlyList<string> ProjectReferences(string projectPath)
        => XDocument.Load(projectPath)
            .Descendants("ProjectReference")
            .Select(reference => reference.Attribute("Include")!.Value.Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static string RepositoryRoot()
        => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
}
