namespace GameServer.Infrastructure.Content;

/// <summary>配置启动内容包路径及文件缺失时是否允许使用内置兼容内容。</summary>
public sealed class GameContentOptions
{
    public const string SectionName = "GameServer:Content";
    public string BootstrapPath { get; set; } = "Content/game-content.v1.json";
    public bool AllowBuiltInFallback { get; set; } = true;
}
