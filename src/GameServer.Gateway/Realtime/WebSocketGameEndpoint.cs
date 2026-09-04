using System.Net.WebSockets;
using System.Security.Claims;
using GameServer.Contracts;
using MessagePack;
using Orleans;

namespace GameServer.Gateway.Realtime;

public static class WebSocketGameEndpoint
{
    public static async Task HandleAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = StatusCodes.Status400BadRequest; return; }
        var characterId = context.Request.Query["characterId"].ToString();
        var accountId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(characterId) || string.IsNullOrWhiteSpace(accountId)) { context.Response.StatusCode = StatusCodes.Status401Unauthorized; return; }
        var grains = context.RequestServices.GetRequiredService<IGrainFactory>();
        if (!await grains.GetGrain<IAccountGrain>(accountId).OwnsCharacterAsync(characterId)) { context.Response.StatusCode = StatusCodes.Status403Forbidden; return; }
        var character = grains.GetGrain<ICharacterGrain>(characterId);
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        while (socket.State == WebSocketState.Open)
        {
            byte[]? bytes;
            try { bytes = await ReceiveAsync(socket, context.RequestAborted); }
            catch (InvalidOperationException) { await SendErrorAsync(socket, "message_too_large", "WebSocket message exceeds 64 KiB.", context.RequestAborted); break; }
            if (bytes is null) break;
            RealtimeEnvelope incoming;
            try { incoming = MessagePackSerializer.Deserialize<RealtimeEnvelope>(bytes); }
            catch (Exception) { await SendErrorAsync(socket, "invalid_envelope", "The binary envelope is invalid.", context.RequestAborted); continue; }
            if (incoming.ProtocolVersion is not (1 or 2)) { await SendErrorAsync(socket, "unsupported_protocol", "Only protocol versions 1 and 2 are supported.", context.RequestAborted); continue; }
            CommandResult result;
            try { result = await DispatchAsync(character, incoming); }
            catch (Exception) { await SendErrorAsync(socket, "invalid_command", "The command payload is invalid.", context.RequestAborted); continue; }
            await SendResultAsync(socket, grains, incoming, result, context.RequestAborted);
        }
    }
    private static Task<CommandResult> DispatchAsync(ICharacterGrain character, RealtimeEnvelope envelope) => (envelope.ProtocolVersion, envelope.MessageId) switch
    {
        (_, RealtimeMessageIds.Move) => character.MoveAsync(MessagePackSerializer.Deserialize<MoveCommand>(envelope.Payload), envelope.OperationId),
        (_, RealtimeMessageIds.Attack) => character.AttackAsync(MessagePackSerializer.Deserialize<AttackCommand>(envelope.Payload), envelope.OperationId),
        (_, RealtimeMessageIds.AcceptQuest) => character.AcceptQuestAsync(MessagePackSerializer.Deserialize<QuestCommand>(envelope.Payload), envelope.OperationId),
        (_, RealtimeMessageIds.CompleteQuest) => character.CompleteQuestAsync(MessagePackSerializer.Deserialize<QuestCommand>(envelope.Payload), envelope.OperationId),
        (2, RealtimeMessageIds.UseSkill) => character.UseSkillAsync(MessagePackSerializer.Deserialize<UseSkillCommand>(envelope.Payload), envelope.OperationId),
        (2, RealtimeMessageIds.Equip) => character.EquipAsync(MessagePackSerializer.Deserialize<EquipCommand>(envelope.Payload), envelope.OperationId),
        (2, RealtimeMessageIds.Unequip) => character.UnequipAsync(MessagePackSerializer.Deserialize<UnequipCommand>(envelope.Payload), envelope.OperationId),
        (2, RealtimeMessageIds.InteractNpc) => character.InteractNpcAsync(MessagePackSerializer.Deserialize<InteractNpcCommand>(envelope.Payload), envelope.OperationId),
        _ => Task.FromResult(new CommandResult(false, "unknown_message"))
    };
    private static async Task SendResultAsync(WebSocket socket, IGrainFactory grains, RealtimeEnvelope incoming, CommandResult result, CancellationToken ct)
    {
        if (!result.Succeeded)
        {
            await SendErrorAsync(socket, result.ErrorCode ?? "command_rejected", "The server rejected this command.", ct, incoming.RequestId, incoming.OperationId, incoming.ProtocolVersion);
            return;
        }
        if (incoming.ProtocolVersion == 1)
        {
            var snapshot = result.Snapshot!;
            await SendAsync(socket, new RealtimeEnvelope { MessageId = RealtimeMessageIds.Snapshot, RequestId = incoming.RequestId, OperationId = incoming.OperationId, ProtocolVersion = 1, Payload = MessagePackSerializer.Serialize(new CharacterSnapshotPayload { Character = snapshot, Zone = await grains.GetGrain<IZoneGrain>(snapshot.ZoneId).GetSnapshotAsync() }) }, ct);
            return;
        }
        var v2 = result.SnapshotV2!;
        var zone = await grains.GetGrain<IZoneGrain>(v2.Character.ZoneId).GetSnapshotAsync();
        await SendAsync(socket, new RealtimeEnvelope { MessageId = RealtimeMessageIds.SnapshotV2, RequestId = incoming.RequestId, OperationId = incoming.OperationId, ProtocolVersion = 2, Payload = MessagePackSerializer.Serialize(new CharacterSnapshotPayloadV2 { Character = v2, Zone = new ZoneSnapshotV2(zone, []), Interaction = result.Interaction }) }, ct);
    }
    private static async Task<byte[]?> ReceiveAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream(); var buffer = new byte[8192]; WebSocketReceiveResult result;
        do { result = await socket.ReceiveAsync(buffer, cancellationToken); if (result.MessageType == WebSocketMessageType.Close) { await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "closed", cancellationToken); return null; } if (stream.Length + result.Count > 64 * 1024) throw new InvalidOperationException(); await stream.WriteAsync(buffer.AsMemory(0, result.Count), cancellationToken); } while (!result.EndOfMessage);
        return stream.ToArray();
    }
    private static Task SendErrorAsync(WebSocket socket, string code, string message, CancellationToken ct, string requestId = "", string operationId = "", int version = 1) => SendAsync(socket, new RealtimeEnvelope { MessageId = RealtimeMessageIds.Error, RequestId = requestId, OperationId = operationId, ProtocolVersion = version, Payload = MessagePackSerializer.Serialize(new ErrorPayload { Code = code, Message = message }) }, ct);
    private static Task SendAsync(WebSocket socket, RealtimeEnvelope envelope, CancellationToken ct) => socket.SendAsync(MessagePackSerializer.Serialize(envelope), WebSocketMessageType.Binary, true, ct);
}
