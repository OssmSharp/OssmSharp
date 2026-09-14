using System.IO.Pipes;
using System.Text.Json;
using OSSMDotNet.Web.ControlCore;
using OSSMDotNet.Web.HostRuntime;
using OSSMDotNet.Web.SessionContracts;
using Microsoft.Extensions.Options;

namespace OSSMDotNet.Web.AdminService;

public sealed class GuestPipeServer(IOptions<AdminOptions> options, PrototypeEngine engine, ILogger<GuestPipeServer> logger) : BackgroundService
{
    private readonly byte[] _serviceHash = CapabilityTokens.Hash(options.Value.GuestServiceToken);
    private readonly List<Task> _connections = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                for (var i = _connections.Count - 1; i >= 0; i--)
                {
                    if (!_connections[i].IsCompleted) continue;
                    await _connections[i];
                    _connections.RemoveAt(i);
                }
                var pipe = new NamedPipeServerStream(options.Value.PipeName, PipeDirection.InOut, 4,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                try { await pipe.WaitForConnectionAsync(stoppingToken); }
                catch { await pipe.DisposeAsync(); throw; }
                _connections.Add(HandleAsync(pipe, stoppingToken));
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            await Task.WhenAll(_connections);
        }
    }

    private async Task HandleAsync(NamedPipeServerStream pipe, CancellationToken stoppingToken)
    {
        await using var owned = pipe;
        string? connectionId = null;
        try
        {
            using var handshake = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            handshake.CancelAfter(TimeSpan.FromSeconds(3));
            var hello = JsonSerializer.Deserialize<PipeHello>(await PipeFrames.ReadAsync(pipe, handshake.Token), GuestProtocol.Json);
            var accepted = hello is not null && hello.V == 1 && CapabilityTokens.Matches(_serviceHash, hello.ServiceToken) &&
                Guid.TryParseExact(hello.ConnectionId, "N", out _);
            await PipeFrames.WriteAsync(pipe, new PipeWelcome(accepted), handshake.Token);
            if (!accepted) return;
            connectionId = hello!.ConnectionId;

            while (!stoppingToken.IsCancellationRequested)
            {
                var call = GuestProtocol.DecodeCall(await PipeFrames.ReadAsync(pipe, stoppingToken));
                if (call.ConnectionId != connectionId)
                    throw new InvalidDataException("wrong-connection");
                GuestReply reply;
                try { reply = Handle(call); }
                catch (ControlRejectedException error) { reply = new(1, call.RequestId, false, error.Code, null, null); }
                await PipeFrames.WriteAsync(pipe, reply, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (OperationCanceledException) { logger.LogWarning("Guest IPC handshake timed out."); }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException)
        {
            logger.LogDebug("Guest IPC closed or rejected.");
        }
        finally
        {
            if (connectionId is not null) engine.TransportLost(connectionId);
        }
    }

    private GuestReply Handle(GuestCall call)
    {
        if (call.Op == "join")
        {
            var joined = engine.Join(call.SessionId, call.Proof, call.ConnectionId);
            logger.LogInformation(new EventId(200, "OperatorActivity"), "Guest connected; waiting for approval.");
            return new(1, call.RequestId, true, "joined", ToGuest(engine.GuestSnapshot(joined.SessionId, joined.GuestToken, joined.ConnectionId)),
                new(joined.SessionId, joined.ConnectionId, joined.GuestToken));
        }
        switch (call.Op)
        {
            case "input":
                var input = call.Input!;
                engine.Submit(call.SessionId, call.Proof, call.ConnectionId, new(
                    input.V, input.Epoch, input.PolicyVersion, input.GrantId, input.Seq, input.Run,
                    new(input.Desired.Pattern, input.Desired.Speed, input.Desired.Stroke, input.Desired.Depth, input.Desired.Sensation)));
                break;
            case "stop":
                engine.GuestStop(call.SessionId, call.Proof, call.ConnectionId);
                break;
            case "leave":
                engine.Leave(call.SessionId, call.Proof, call.ConnectionId);
                return new(1, call.RequestId, true, "ended", null, null);
            case "status":
                break;
            default:
                throw new ControlRejectedException("operation-not-allowed");
        }
        return new(1, call.RequestId, true, "ok",
            ToGuest(engine.GuestSnapshot(call.SessionId, call.Proof, call.ConnectionId)), null);
    }

    private static GuestStatus ToGuest(ControlSnapshot state) => new(
        state.Mode, state.Reason, state.Generation, state.LockRevision, state.BridgeReady, state.DeviceMoving,
        new(state.Desired.Pattern, state.Desired.Speed, state.Desired.Stroke, state.Desired.Depth, state.Desired.Sensation),
        state.Position, state.Velocity, new(state.Limits.SpeedCap, state.Limits.MaximumDepth),
        state.PolicyVersion, state.SessionMode, state.ConnectionId, state.AckSeq,
        state.Grant is { } grant ? new(grant.Id, grant.Epoch, grant.PolicyVersion, grant.ValidForMs) : null)
    {
        Current = state.Current is { } current
            ? new(current.Pattern, current.Speed, current.Stroke, current.Depth, current.Sensation) : null,
        VerifiedSeq = state.VerifiedSeq,
        DeviceEnabled = state.DeviceEnabled,
        IsSimulator = state.IsSimulator
    };
}
