using System.Security.Cryptography;
using System.Text.Json;

namespace GameServer.Domain.Gameplay;

/// <summary>为角色操作和命令参数生成稳定标识，隔离不同角色的客户端操作号。</summary>
public static class OperationIdentity
{
    public static bool IsValid(string operationId) => !string.IsNullOrWhiteSpace(operationId) && operationId.Length <= 128;
    public static string Scope(string characterId, string operationId) => Hash(new[] { characterId, operationId });
    public static string Fingerprint<T>(string kind, T command) => Hash(new { Kind = kind, Command = command });
    private static string Hash<T>(T value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
}
