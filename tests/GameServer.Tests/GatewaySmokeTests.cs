using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using GameServer.Contracts;
using MessagePack;

namespace GameServer.Tests;

/// <summary>通过真实 HTTP 和 WebSocket 完成玩法闭环，并在外部重启 Gateway 后验证持久化。</summary>
public sealed class GatewaySmokeTests
{
    [GatewayFact("prepare")]
    public async Task Register_login_play_and_persist_character()
    {
        using var http = CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/api/characters/")).StatusCode);
        var account = new SmokeAccount($"smoke-{Guid.NewGuid():N}@example.test", $"Test1!{Guid.NewGuid():N}", "");
        using var registered = await http.PostAsJsonAsync("/api/auth/register", new { account.Email, account.Password });
        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);
        await LoginAsync(http, account);
        using var created = await http.PostAsJsonAsync("/api/characters/", new { name = "SmokeHero", classId = "adventurer" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var character = (await created.Content.ReadFromJsonAsync<CreateCharacterResult>())!.Character!;
        account = account with { CharacterId = character.CharacterId };
        using var entered = await http.PostAsJsonAsync($"/api/characters/{account.CharacterId}/enter", new { zoneId = "starter-plains" });
        entered.EnsureSuccessStatusCode();
        Assert.True((await entered.Content.ReadFromJsonAsync<CommandResult>())!.Succeeded);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync("/api/characters/not-owned")).StatusCode);
        using var socket = await ConnectAsync(http, account.CharacterId);
        var moved = await SendAsync(socket, RealtimeMessageIds.Move, new MoveCommand { X = 2, Y = 0 }, "move", 1);
        Assert.Equal(RealtimeMessageIds.Snapshot, moved.MessageId);
        Assert.Equal(2, MessagePackSerializer.Deserialize<CharacterSnapshotPayload>(moved.Payload).Character.Position.X);
        // 重复运行独立测试项目时，上一轮击杀的怪物可能仍在重生窗口。
        for (var attempt = 0; attempt < 35 && MessagePackSerializer.Deserialize<CharacterSnapshotPayload>(moved.Payload).Zone.Entities.Single(x => x.EntityId == "green-slime").Health == 0; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(1));
            moved = await SendAsync(socket, RealtimeMessageIds.Move, new MoveCommand { X = 2, Y = 0 }, "move", 1);
        }
        Assert.Equal(50, MessagePackSerializer.Deserialize<CharacterSnapshotPayload>(moved.Payload).Zone.Entities.Single(x => x.EntityId == "green-slime").Health);
        Assert.Equal(RealtimeMessageIds.SnapshotV2, (await SendAsync(socket, RealtimeMessageIds.AcceptQuest, new QuestCommand { QuestId = "slime-hunt", NpcId = "guard-aria" }, "accept")).MessageId);
        Assert.Equal(RealtimeMessageIds.SnapshotV2, (await SendAsync(socket, RealtimeMessageIds.Attack, new AttackCommand { TargetMonsterId = "green-slime" }, "hit")).MessageId);
        Assert.Equal(RealtimeMessageIds.SnapshotV2, (await SendAsync(socket, RealtimeMessageIds.UseSkill, new UseSkillCommand { SkillId = "power-strike", TargetMonsterId = "green-slime" }, "kill")).MessageId);
        var completed = await SendAsync(socket, RealtimeMessageIds.CompleteQuest, new QuestCommand { QuestId = "slime-hunt", NpcId = "guard-aria" }, "complete");
        Assert.Equal(RealtimeMessageIds.SnapshotV2, completed.MessageId);
        AssertRewards(MessagePackSerializer.Deserialize<CharacterSnapshotPayloadV2>(completed.Payload).Character.Character);
        var duplicate = await SendAsync(socket, RealtimeMessageIds.CompleteQuest, new QuestCommand { QuestId = "slime-hunt", NpcId = "guard-aria" }, "complete");
        AssertRewards(MessagePackSerializer.Deserialize<CharacterSnapshotPayloadV2>(duplicate.Payload).Character.Character);
        var invalid = await SendAsync(socket, RealtimeMessageIds.Move, new MoveCommand { X = 3, Y = 0 }, "move");
        Assert.Equal("operation_conflict", MessagePackSerializer.Deserialize<ErrorPayload>(invalid.Payload).Code);
        var unsupported = await SendAsync(socket, RealtimeMessageIds.Move, new MoveCommand { X = 2, Y = 0 }, "future", 99);
        Assert.Equal("unsupported_protocol", MessagePackSerializer.Deserialize<ErrorPayload>(unsupported.Payload).Code);
        var path = StatePath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(account));
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    }

    [GatewayFact("verify")]
    public async Task Restart_preserves_identity_rewards_quests_and_operation_receipts()
    {
        var account = JsonSerializer.Deserialize<SmokeAccount>(await File.ReadAllTextAsync(StatePath()))!;
        using var http = CreateClient();
        await LoginAsync(http, account);
        var snapshot = await http.GetFromJsonAsync<CharacterSnapshot>($"/api/characters/{account.CharacterId}");
        AssertRewards(snapshot!);
        using var socket = await ConnectAsync(http, account.CharacterId);
        var replay = await SendAsync(socket, RealtimeMessageIds.CompleteQuest, new QuestCommand { QuestId = "slime-hunt", NpcId = "guard-aria" }, "complete");
        Assert.Equal(RealtimeMessageIds.SnapshotV2, replay.MessageId);
        AssertRewards(MessagePackSerializer.Deserialize<CharacterSnapshotPayloadV2>(replay.Payload).Character.Character);
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    }

    private static void AssertRewards(CharacterSnapshot snapshot)
    {
        Assert.Equal(1, snapshot.Inventory.Where(x => x.ItemId == "slime-gel").Sum(x => x.Quantity));
        Assert.Equal(1, snapshot.Inventory.Where(x => x.ItemId == "traveler-token").Sum(x => x.Quantity));
        Assert.True(snapshot.Quests.Single(x => x.QuestId == "slime-hunt").IsCompleted);
    }
    private static HttpClient CreateClient() => new() { BaseAddress = new Uri(Environment.GetEnvironmentVariable("GameTests__Gateway")!), Timeout = TimeSpan.FromSeconds(30) };
    private static string StatePath() => Path.GetFullPath(Environment.GetEnvironmentVariable("GameTests__StateFile") ?? "TestResults/smoke-state.json");
    private static async Task LoginAsync(HttpClient http, SmokeAccount account)
    {
        using var response = await http.PostAsJsonAsync("/api/auth/login", new { account.Email, account.Password });
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", json.GetProperty("accessToken").GetString());
    }
    private static async Task<ClientWebSocket> ConnectAsync(HttpClient http, string characterId)
    {
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", http.DefaultRequestHeaders.Authorization!.ToString());
        var uri = new UriBuilder(http.BaseAddress!) { Scheme = http.BaseAddress!.Scheme == "https" ? "wss" : "ws", Path = "/ws", Query = "characterId=" + Uri.EscapeDataString(characterId) };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await socket.ConnectAsync(uri.Uri, timeout.Token);
        return socket;
    }
    private static async Task<RealtimeEnvelope> SendAsync<T>(ClientWebSocket socket, int messageId, T command, string operationId, int version = 2)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var requestId = Guid.NewGuid().ToString("N");
        var envelope = new RealtimeEnvelope { MessageId = messageId, RequestId = requestId, OperationId = operationId, ProtocolVersion = version, Payload = MessagePackSerializer.Serialize(command) };
        await socket.SendAsync(MessagePackSerializer.Serialize(envelope), WebSocketMessageType.Binary, true, timeout.Token);
        using var stream = new MemoryStream();
        var buffer = new byte[8192];
        WebSocketReceiveResult received;
        do
        {
            received = await socket.ReceiveAsync(buffer, timeout.Token);
            Assert.Equal(WebSocketMessageType.Binary, received.MessageType);
            await stream.WriteAsync(buffer.AsMemory(0, received.Count), timeout.Token);
            Assert.True(stream.Length <= 1024 * 1024);
        } while (!received.EndOfMessage);
        var response = MessagePackSerializer.Deserialize<RealtimeEnvelope>(stream.ToArray());
        if (version is 1 or 2)
        {
            Assert.Equal(requestId, response.RequestId);
            Assert.Equal(operationId, response.OperationId);
            Assert.Equal(version, response.ProtocolVersion);
        }
        return response;
    }

    /// <summary>按环境变量选择重启前或重启后的真实网关测试阶段。</summary>
    public sealed class GatewayFactAttribute : FactAttribute
    {
        public GatewayFactAttribute(string phase)
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GameTests__Gateway")) || Environment.GetEnvironmentVariable("GameTests__Phase") != phase)
                Skip = $"Run scripts/Test-Integration.ps1 to execute the {phase} gateway phase.";
        }
    }

    /// <summary>保存本地测试账号标识，供重启后的独立测试进程登录核对。</summary>
    public sealed record SmokeAccount(string Email, string Password, string CharacterId);
}
