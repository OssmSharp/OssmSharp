using System.IO.Pipes;
using System.Text.Json;
using Microsoft.Extensions.Options;
using OSSMDotNet.Web.HostRuntime;
using OSSMDotNet.Web.SessionContracts;

namespace OSSMDotNet.Web.GuestWeb.Services;

public sealed class GuestSessionClient(IOptions<GuestOptions> options, ILogger<GuestSessionClient> logger) : IAsyncDisposable
{
    private static readonly TimeSpan RequestDeadline = TimeSpan.FromSeconds(2);
    private readonly GuestOptions _options = options.Value;
    private readonly string _connectionId = Guid.NewGuid().ToString("N");
    private readonly SemaphoreSlim _rpcGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _lifecycleLock = new();
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private NamedPipeClientStream? _pipe;
    private string? _sessionId;
    private string? _guestToken;
    private string? _terminalCode;
    private bool _joinAttempted;
    private int _activeCalls;
    private long _revision;
    private Task? _disposeTask;

    public bool IsJoined
    {
        get { lock (_lifecycleLock) return _terminalCode is null && _guestToken is not null; }
    }

    public bool IsExpired
    {
        get { lock (_lifecycleLock) return _terminalCode is not null; }
    }

    public string? FailureCode
    {
        get { lock (_lifecycleLock) return _terminalCode; }
    }

    public Task<GuestResult> JoinAsync(string sessionId, string invitation, CancellationToken cancellationToken = default) =>
        CallAsync(GuestOperation.Join, sessionId, invitation, null, cancellationToken);

    public Task<GuestResult> StatusAsync(CancellationToken cancellationToken = default) =>
        CallAsync(GuestOperation.Status, null, null, null, cancellationToken);

    public Task<GuestResult> SubmitInputAsync(GuestInput input, CancellationToken cancellationToken = default) =>
        CallAsync(GuestOperation.Input, null, null, input, cancellationToken);

    public Task<GuestResult> StopAsync(CancellationToken cancellationToken = default) =>
        CallAsync(GuestOperation.Stop, null, null, null, cancellationToken);

    public Task<GuestResult> LeaveAsync(CancellationToken cancellationToken = default) =>
        CallAsync(GuestOperation.Leave, null, null, null, cancellationToken);

    private async Task<GuestResult> CallAsync(
        GuestOperation operation, string? sessionId, string? invitation, GuestInput? input, CancellationToken cancellationToken)
    {
        CancellationToken lifetimeToken;
        lock (_lifecycleLock)
        {
            if (_terminalCode is not null)
                return Failure(_terminalCode);
            lifetimeToken = _lifetime.Token;
            _activeCalls++;
        }

        var ownsGate = false;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetimeToken);
        deadline.CancelAfter(RequestDeadline);
        try
        {
            // The deadline includes queueing, connection, handshake, write, and reply. Nothing is retried.
            await _rpcGate.WaitAsync(deadline.Token);
            ownsGate = true;
            if (FailureCode is { } terminal)
                return Failure(terminal);

            if (operation == GuestOperation.Join)
            {
                if (_joinAttempted)
                    return Failure("join-already-attempted");
                if (!GuestProtocol.ValidText(sessionId, 64) || !GuestProtocol.ValidText(invitation, 128))
                    return Failure("invalid-invitation");

                _joinAttempted = true;
                var connectingPipe = new NamedPipeClientStream(
                    ".", _options.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                lock (_lifecycleLock)
                {
                    if (_terminalCode is not null)
                    {
                        connectingPipe.Dispose();
                        return Failure(_terminalCode);
                    }
                    _pipe = connectingPipe;
                }
                await connectingPipe.ConnectAsync(deadline.Token);
                await PipeFrames.WriteAsync(connectingPipe, new PipeHello(1, _options.GuestServiceToken, _connectionId), deadline.Token);
                ReadOnlyMemory<byte> welcomeBytes = await PipeFrames.ReadAsync(connectingPipe, deadline.Token);
                if (!DecodeWelcome(welcomeBytes.Span))
                    return Expire("handshake-rejected");
            }
            else
            {
                lock (_lifecycleLock)
                {
                    if (_guestToken is null || _sessionId is null)
                        return Failure(_terminalCode ?? "not-joined");
                    sessionId = _sessionId;
                    invitation = _guestToken;
                }
            }

            // Session/proof identifiers are already bound and validated. Check typed input without serializing it twice.
            if (operation == GuestOperation.Input && !IsValidInput(input))
                return Failure("invalid-input");

            var call = new GuestCall(1, Guid.NewGuid().ToString("N"), WireOperation(operation),
                sessionId!, invitation!, _connectionId, input);
            var pipe = _pipe ?? throw new IOException("pipe-closed");
            deadline.Token.ThrowIfCancellationRequested();
            await PipeFrames.WriteAsync(pipe, call, deadline.Token);
            ReadOnlyMemory<byte> replyBytes = await PipeFrames.ReadAsync(pipe, deadline.Token);
            var reply = DecodeReply(replyBytes.Span, call);
            deadline.Token.ThrowIfCancellationRequested();

            if (operation == GuestOperation.Join && reply.Ok)
            {
                lock (_lifecycleLock)
                {
                    if (_terminalCode is not null)
                        return Failure(_terminalCode);
                    _sessionId = reply.Join!.SessionId;
                    _guestToken = reply.Join.GuestToken;
                }
            }

            var result = new GuestResult(reply.Ok, reply.Code, reply.State, ++_revision);
            if (!reply.Ok && reply.Code is "guest-unauthorized" or "session-expired")
                Expire(reply.Code);
            if (operation == GuestOperation.Leave || (operation == GuestOperation.Join && !reply.Ok))
                Expire(operation == GuestOperation.Leave ? "session-ended" : reply.Code);
            return result;
        }
        catch (OperationCanceledException)
        {
            return Expire(FailureCode ?? (cancellationToken.IsCancellationRequested ? "request-cancelled" : "pipe-timeout"));
        }
        catch (TimeoutException)
        {
            return Expire("pipe-timeout");
        }
        catch (JsonException)
        {
            return Expire("invalid-reply");
        }
        catch (InvalidDataException)
        {
            return Expire("invalid-reply");
        }
        catch (IOException)
        {
            return Expire("pipe-disconnected");
        }
        catch (UnauthorizedAccessException)
        {
            return Expire("pipe-unavailable");
        }
        catch (ObjectDisposedException)
        {
            return Expire("pipe-disconnected");
        }
        finally
        {
            if (ownsGate)
                _rpcGate.Release();
            lock (_lifecycleLock)
            {
                _activeCalls--;
                if (_activeCalls == 0 && _terminalCode is not null)
                    _drained.TrySetResult();
            }
        }
    }

    private static bool IsValidInput(GuestInput? input) =>
        input is { V: 2, Run: true, Desired: not null, PolicyVersion: >= 1, Seq: >= 1 and <= 9_007_199_254_740_991 } &&
        GuestProtocol.ValidText(input.Epoch, 64) && GuestProtocol.ValidText(input.GrantId, 64);

    private static bool DecodeWelcome(ReadOnlySpan<byte> bytes) =>
        (JsonSerializer.Deserialize<PipeWelcome>(bytes, GuestProtocol.Json)
            ?? throw new InvalidDataException("invalid-welcome")).Accepted;

    private static GuestReply DecodeReply(ReadOnlySpan<byte> bytes, GuestCall call)
    {
        var reply = JsonSerializer.Deserialize<GuestReply>(bytes, GuestProtocol.Json) ??
            throw new InvalidDataException("invalid-reply");
        if (reply.V != 1 || !string.Equals(reply.RequestId, call.RequestId, StringComparison.Ordinal) ||
            !IsValidCode(reply.Code))
            throw new InvalidDataException("invalid-reply");
        if (reply.State is { } decodedState &&
            (!GuestProtocol.ValidText(decodedState.Mode, 64) || decodedState.Reason is not { Length: <= 256 } ||
             !GuestProtocol.ValidText(decodedState.SessionMode, 64)))
            throw new InvalidDataException("invalid-state");

        if (call.Op == "join" && reply.Ok)
        {
            if (reply.Join is not { } joined ||
                !string.Equals(joined.SessionId, call.SessionId, StringComparison.Ordinal) ||
                !string.Equals(joined.ConnectionId, call.ConnectionId, StringComparison.Ordinal) ||
                !GuestProtocol.ValidText(joined.GuestToken, 128))
                throw new InvalidDataException("invalid-join");
        }
        else if (reply.Join is not null)
        {
            throw new InvalidDataException("unexpected-join");
        }
        return reply;
    }

    private static bool IsValidCode(string? code)
    {
        if (!GuestProtocol.ValidText(code, 128))
            return false;
        foreach (var character in code.AsSpan())
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_'))
                return false;
        }
        return true;
    }

    private GuestResult Expire(string code)
    {
        lock (_lifecycleLock)
        {
            if (_terminalCode is not null)
                return Failure(_terminalCode);
            _terminalCode = code;
            _guestToken = null;
            _sessionId = null;
            _lifetime.Cancel();
            _pipe?.Dispose();
            _pipe = null;
            logger.LogInformation("Guest connection closed; retained authority was discarded.");
            return Failure(code);
        }
    }

    private static GuestResult Failure(string code) => new(false, code, null);

    private static string WireOperation(GuestOperation operation) => operation switch
    {
        GuestOperation.Join => "join",
        GuestOperation.Status => "status",
        GuestOperation.Input => "input",
        GuestOperation.Stop => "stop",
        GuestOperation.Leave => "leave",
        _ => throw new InvalidOperationException("operation-not-allowed")
    };

    public ValueTask DisposeAsync()
    {
        lock (_lifecycleLock)
        {
            if (_disposeTask is not null)
                return new(_disposeTask);
            Expire("session-closed");
            if (_activeCalls == 0)
                _drained.TrySetResult();
            _disposeTask = DisposeResourcesAsync();
            return new(_disposeTask);
        }
    }

    private async Task DisposeResourcesAsync()
    {
        await _drained.Task.ConfigureAwait(false);
        _rpcGate.Dispose();
        _lifetime.Dispose();
    }

}
