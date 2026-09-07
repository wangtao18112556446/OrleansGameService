namespace GameServer.Gateway.Configuration;

/// <summary>配置 JWT 签发与验证参数，并为令牌时长和时钟偏差提供兼容默认值。</summary>
public sealed class JwtOptions
{
    public const string SectionName = "Jwt";
    public string Issuer { get; set; } = string.Empty;
    public string Audience { get; set; } = string.Empty;
    public string SigningKey { get; set; } = string.Empty;
    public TimeSpan AccessTokenLifetime { get; set; } = TimeSpan.FromHours(8);
    public TimeSpan ClockSkew { get; set; } = TimeSpan.FromMinutes(1);
}

/// <summary>配置 Orleans 集群标识、服务标识以及本地或 ADO.NET 运行模式。</summary>
public sealed class GameOrleansOptions
{
    public const string SectionName = "Orleans";
    public bool UseAdoNet { get; set; } = true;
    public string ClusterId { get; set; } = "orleans-game";
    public string ServiceId { get; set; } = "orleans-game";
}

/// <summary>限制实时连接的单条入站消息大小及分片接收缓冲区。</summary>
public sealed class RealtimeOptions
{
    public const string SectionName = "GameServer:Realtime";
    public int MaxMessageBytes { get; set; } = 64 * 1024;
    public int ReceiveBufferBytes { get; set; } = 8 * 1024;
}

/// <summary>配置网关按远端地址执行的全局固定窗口限流。</summary>
public sealed class GameRateLimitOptions
{
    public const string SectionName = "GameServer:RateLimit";
    public int PermitLimit { get; set; } = 120;
    public TimeSpan Window { get; set; } = TimeSpan.FromMinutes(1);
}
