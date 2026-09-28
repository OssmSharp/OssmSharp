using System.Buffers;
using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using OssmSharp.Device.Runtime;
using OssmSharp.Device.Simulator.Models;

namespace OssmSharp.Device.Simulator;

/// <summary>
/// Controls the native OSSM-RS simulator through its bounded JSON-lines protocol.
/// </summary>
public sealed class SimulatedDevice : DeviceBase
{
    private const int MaximumPendingRequests = 8;
    private const int MaximumFrameBytes = 4096;
    private static readonly byte[] NewLine = [(byte)'\n'];
    private static readonly DeviceInformation Information = new(
        "ossm-rs simulator",
        "ossm-rs-sim-v1",
        null,
        [MotionPattern.SimpleStroke]);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        AllowDuplicateProperties = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        MaxDepth = 12,
        PropertyNameCaseInsensitive = false,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private readonly object _admissionGate = new();
    private readonly ConcurrentDictionary<long, TaskCompletionSource<SimulatorReply>> _pending = new();
    private readonly SemaphoreSlim _inputGate = new(1, 1);
    private readonly TaskCompletionSource _sendsSettled = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SimulatedDeviceOptions _options;
    private readonly ILogger<SimulatedDevice> _logger;
    private readonly TimeProvider _timeProvider;
    private NetworkStream? _stream;
    private long _requestId;
    private long _connectionGeneration;
    private long _lastSampleSequence;
    private long _lastDisconnectedRequestId;
    private int _activeSends;
    private bool? _reportedConnected;
    private bool _disconnecting;
    private bool _closing;

    /// <summary>
    /// Initializes a new instance of the <see cref="SimulatedDevice"/> class.
    /// </summary>
    /// <param name="options">The validated simulator options.</param>
    /// <param name="timeProvider">The monotonic and UTC time source.</param>
    /// <param name="logger">The operational logger.</param>
    public SimulatedDevice(
        IOptions<SimulatedDeviceOptions> options,
        TimeProvider timeProvider,
        ILogger<SimulatedDevice> logger)
        : this(ValidateOptions(options), timeProvider, logger)
    {
    }

    private SimulatedDevice(
        SimulatedDeviceOptions options,
        TimeProvider timeProvider,
        ILogger<SimulatedDevice> logger)
        : base(timeProvider, logger)
    {
        _options = options;
        _logger = logger;
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    protected override async Task ConnectAsync(CancellationToken cancellationToken)
    {
        using var client = new TcpClient(AddressFamily.InterNetwork);
        await client.ConnectAsync(IPAddress.Loopback, new Uri(_options.Endpoint).Port, cancellationToken)
            .ConfigureAwait(false);
        var stream = client.GetStream();
        lock (_admissionGate)
        {
            _stream = stream;
        }

        _logger.Connected();
        var reader = ReadOutputAsync(stream, cancellationToken);
        try
        {
            await SendAsync("status", null, cancellationToken).ConfigureAwait(false);
            await reader.ConfigureAwait(false);
        }
        finally
        {
            lock (_admissionGate)
            {
                _closing = true;
                if (_activeSends == 0)
                {
                    _sendsSettled.TrySetResult();
                }
            }

            await _inputGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                _stream?.Dispose();
                CompletePending(new IOException("The simulator connection closed."));
                try
                {
                    await reader.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                }
                catch (IOException) when (cancellationToken.IsCancellationRequested)
                {
                }
            }
            finally
            {
                _inputGate.Release();
                await _sendsSettled.Task.ConfigureAwait(false);
                lock (_admissionGate)
                {
                    _stream = null;
                }

                PublishDisconnected();
                _logger.Disconnected();
            }
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// The simulator request is correlated with its reply, so the whole request uses the settlement token; revocation
    /// is checked by the device before dispatch.
    /// </remarks>
    protected override Task DispatchMotionAsync(
        MotionSettings motionSettings,
        CancellationToken requestCancellationToken,
        CancellationToken shutdownToken)
    {
        ThrowIfUnsupported(motionSettings);
        var desired = new SimulatorDesired(
            (int)motionSettings.Pattern,
            motionSettings.Speed,
            motionSettings.Stroke,
            motionSettings.Depth,
            motionSettings.Sensation);
        return SendAsync("write", desired, shutdownToken);
    }

    /// <inheritdoc />
    protected override void ValidateMotion(MotionSettings settings)
    {
        try
        {
            ThrowIfUnsupported(settings);
        }
        catch (NotSupportedException exception)
        {
            throw new InvalidOperationException(exception.Message, exception);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Sends the simulator's speed-only <c>stop</c> command, so a Stop never depends on the other reported settings
    /// being writable.
    /// </remarks>
    protected override Task DispatchSafetyStopAsync(
        CancellationToken requestCancellationToken,
        CancellationToken shutdownToken)
    {
        requestCancellationToken.ThrowIfCancellationRequested();
        return SendAsync("stop", null, shutdownToken);
    }

    /// <summary>
    /// Applies an external simulator state change for integration testing.
    /// </summary>
    /// <param name="motionSettings">The complete external simulator settings.</param>
    /// <returns>Completion after the simulator accepts the state change.</returns>
    internal Task InjectExternalAsync(
        MotionSettings motionSettings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(motionSettings);
        Validator.ValidateObject(
            motionSettings,
            new ValidationContext(motionSettings),
            validateAllProperties: true);
        if (motionSettings.Pattern != MotionPattern.SimpleStroke)
        {
            throw new NotSupportedException("The simulator currently supports only Simple Stroke.");
        }

        var desired = new SimulatorDesired(
            (int)motionSettings.Pattern,
            motionSettings.Speed,
            motionSettings.Stroke,
            motionSettings.Depth,
            motionSettings.Sensation);
        return SendAsync("external", desired, cancellationToken);
    }

    /// <summary>
    /// Sets the simulator response delay for integration testing.
    /// </summary>
    /// <param name="milliseconds">The delay in milliseconds.</param>
    /// <returns>Completion after the simulator accepts the delay.</returns>
    internal Task SetDelayAsync(int milliseconds, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(milliseconds);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(milliseconds, 5000);
        return SendAsync("delay", null, cancellationToken, milliseconds);
    }

    /// <summary>
    /// Freezes or resumes simulator state updates for integration testing.
    /// </summary>
    /// <param name="freeze"><see langword="true"/> to freeze updates; otherwise, <see langword="false"/>.</param>
    /// <returns>Completion after the simulator accepts the setting.</returns>
    internal Task SetStaleAsync(bool freeze, CancellationToken cancellationToken) =>
        SendAsync("stale", null, cancellationToken, freeze ? 1 : 0);

    /// <summary>
    /// Disconnects the simulator for integration testing.
    /// </summary>
    /// <returns>Completion after the simulator disconnects.</returns>
    internal async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        lock (_admissionGate)
        {
            _connectionGeneration++;
            _disconnecting = true;
        }

        await SendAsync("disconnect", null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reconnects the simulator for integration testing.
    /// </summary>
    /// <returns>Completion after the simulator reconnects.</returns>
    internal async Task ReconnectAsync(CancellationToken cancellationToken)
    {
        await SendAsync("connect", null, cancellationToken).ConfigureAwait(false);
        lock (_admissionGate)
        {
            if (_reportedConnected is not true)
            {
                throw new IOException("The simulator did not report a connected state.");
            }

            _disconnecting = false;
        }

    }

    /// <inheritdoc />
    protected override async ValueTask DisposeCoreAsync()
    {
        _inputGate.Dispose();
        await base.DisposeCoreAsync().ConfigureAwait(false);
    }

    private static void ThrowIfUnsupported(MotionSettings motionSettings)
    {
        if (motionSettings.Pattern != MotionPattern.SimpleStroke)
            throw new NotSupportedException("The simulator currently supports only Simple Stroke.");
        if (motionSettings.Sensation != 50)
            throw new NotSupportedException("The simulator requires sensation 50 for ordinary writes.");
        if (motionSettings.Buffer != 0)
            throw new NotSupportedException("The simulator supports buffer 0 only.");
    }

    private static SimulatorReply DeserializeReply(ReadOnlySpan<byte> line)
    {
        if (IsEvent(line))
        {
            var message = JsonSerializer.Deserialize<SimulatorEvent>(line, Json)
                ?? throw new JsonException("The simulator returned an empty event.");
            return new SimulatorReply(message.V, null, null, null, message.Event, message.State);
        }

        var response = JsonSerializer.Deserialize<SimulatorResponse>(line, Json)
            ?? throw new JsonException("The simulator returned an empty response.");
        return new SimulatorReply(response.V, response.Id, response.Ok, response.Error, null, response.State);
    }

    private static bool IsEvent(ReadOnlySpan<byte> line)
    {
        var reader = new Utf8JsonReader(line);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("The simulator returned a non-object response.");
        }

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException("The simulator returned an invalid response object.");
            }

            var isEvent = reader.ValueTextEquals("event"u8);
            if (!reader.Read())
            {
                throw new JsonException("The simulator returned an incomplete response object.");
            }

            if (isEvent)
            {
                return true;
            }

            reader.Skip();
        }

        return false;
    }

    private async Task SendAsync(
        string operation,
        SimulatorDesired? desired,
        CancellationToken cancellationToken,
        int? value = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        long id;
        long connectionGeneration;
        NetworkStream stream;
        TaskCompletionSource<SimulatorReply> completion;
        lock (_admissionGate)
        {
            if (_closing || _stream is not { } connectedStream)
            {
                throw new IOException("The simulator is unavailable.");
            }

            if ((_disconnecting || _reportedConnected is false) && operation is ("write" or "stop"))
            {
                throw new IOException("The simulator is disconnected.");
            }

            if (_pending.Count >= MaximumPendingRequests)
            {
                throw new InvalidOperationException("The simulator request capacity is exhausted.");
            }

            stream = connectedStream;
            connectionGeneration = _connectionGeneration;
            id = Interlocked.Increment(ref _requestId);
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_pending.TryAdd(id, completion))
            {
                throw new InvalidOperationException("A duplicate simulator request identifier was generated.");
            }

            _activeSends++;
        }

        try
        {
            var request = new SimulatorRequest(
                1,
                id,
                operation,
                operation is "write" or "stop" ? id : null,
                desired,
                value);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(request, Json);
            if (bytes.Length > MaximumFrameBytes)
            {
                throw new InvalidDataException("The simulator request exceeds the protocol frame limit.");
            }

            await _inputGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                lock (_admissionGate)
                {
                    if (_closing || !ReferenceEquals(_stream, stream))
                    {
                        throw new IOException("The simulator connection is closing.");
                    }

                    if (connectionGeneration != _connectionGeneration)
                    {
                        throw new IOException("The simulator connection changed before the request was sent.");
                    }
                }

                cancellationToken.ThrowIfCancellationRequested();
                await stream.WriteAsync(bytes, CancellationToken.None).ConfigureAwait(false);
                await stream.WriteAsync(NewLine, CancellationToken.None).ConfigureAwait(false);
                await stream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                _inputGate.Release();
            }

            var reply = await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (reply.Ok is not true)
            {
                throw MapRejection(reply.Error);
            }
        }
        catch
        {
            _pending.TryRemove(id, out _);
            throw;
        }
        finally
        {
            lock (_admissionGate)
            {
                if (--_activeSends == 0 && _closing)
                {
                    _sendsSettled.TrySetResult();
                }
            }
        }
    }

    private async Task ReadOutputAsync(Stream output, CancellationToken cancellationToken)
    {
        using var owner = MemoryPool<byte>.Shared.Rent(MaximumFrameBytes + 1);
        var buffer = owner.Memory[..(MaximumFrameBytes + 1)];
        var used = 0;
        try
        {
            while (true)
            {
                var read = await output.ReadAsync(buffer[used..], cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    throw new IOException("The simulator output stream closed.");
                }

                used += read;
                var consumed = ConsumeLines(buffer.Span[..used]);
                buffer.Span[consumed..used].CopyTo(buffer.Span);
                used -= consumed;
                if (used == buffer.Length)
                {
                    throw new InvalidDataException("The simulator response exceeds the protocol frame limit.");
                }
            }
        }
        catch (Exception exception) when (
            exception is IOException or InvalidDataException or JsonException)
        {
            var mapped = exception is JsonException
                ? new InvalidDataException("The simulator returned malformed JSON.", exception)
                : exception;
            lock (_admissionGate)
            {
                _closing = true;
            }

            CompletePending(mapped);
            throw mapped;
        }
    }

    private int ConsumeLines(ReadOnlySpan<byte> buffer)
    {
        var consumed = 0;
        while (buffer[consumed..].IndexOf((byte)'\n') is var offset && offset >= 0)
        {
            if (offset > MaximumFrameBytes)
            {
                throw new InvalidDataException("The simulator response exceeds the protocol frame limit.");
            }

            var line = buffer.Slice(consumed, offset);
            if (!line.IsEmpty && line[^1] == '\r')
            {
                line = line[..^1];
            }

            var reply = DeserializeReply(line);
            ProcessReply(reply);
            consumed += offset + 1;
        }

        return consumed;
    }

    /// <summary>
    /// Applies one decoded native simulator reply to connection state and pending requests.
    /// </summary>
    /// <param name="reply">The reply received from the native simulator process.</param>
    internal void ProcessReply(SimulatorReply reply)
    {
        reply.Validate();

        long disconnectedThrough = 0;
        if (reply.Ok is not false &&
            reply.State is { } state &&
            state.SampleSequence > _lastSampleSequence)
        {
            _lastSampleSequence = state.SampleSequence;
            lock (_admissionGate)
            {
                if (_reportedConnected != state.Connected)
                {
                    _reportedConnected = state.Connected;
                    _connectionGeneration++;
                }
            }

            if (state.Connected)
            {
                _lastDisconnectedRequestId = 0;
                PublishConnected(Information);
            }
            else
            {
                disconnectedThrough = reply.Id ?? (_lastDisconnectedRequestId is not 0
                    ? _lastDisconnectedRequestId
                    : Volatile.Read(ref _requestId));
                _lastDisconnectedRequestId = disconnectedThrough;
                PublishDisconnected();
            }

            var settings = new MotionSettings(
                state.Connected ? MotionDeviceState.StrokeEngineIdle : MotionDeviceState.Idle,
                state.Desired.Speed,
                state.Desired.Stroke,
                state.Desired.Sensation,
                state.Desired.Depth,
                0,
                (MotionPattern)state.Desired.Pattern,
                state.Position);
            bool? isOwnWrite = reply.Id is not null && state.CommandId == reply.Id
                ? true
                : state.CommandId is null ? false : null;
            PublishState(
                new DeviceState
                {
                    Timestamp = _timeProvider.GetUtcNow().UtcDateTime,
                    MotionSettings = settings,
                    SessionId = Guid.Empty,
                    VelocityMillimetresPerSecond = state.Velocity,
                },
                isOwnWrite);
        }

        if (reply.Id is { } id && _pending.TryRemove(id, out var completion))
        {
            completion.TrySetResult(reply);
        }

        if (disconnectedThrough is not 0)
        {
            CompletePending(new IOException("The simulator disconnected with unconfirmed requests."),
                disconnectedThrough);
        }
    }

    private void CompletePending(Exception exception, long lastRequestId = long.MaxValue)
    {
        foreach (var pair in _pending)
        {
            if (pair.Key <= lastRequestId && _pending.TryRemove(pair.Key, out var completion))
            {
                completion.TrySetException(exception);
            }
        }
    }

    private static SimulatedDeviceOptions ValidateOptions(IOptions<SimulatedDeviceOptions>? options)
    {
        var value = options?.Value ?? throw new ArgumentNullException(nameof(options));
        Validator.ValidateObject(value, new ValidationContext(value), validateAllProperties: true);
        return value;
    }

    private static Exception MapRejection(string? error) => error switch
    {
        "invalid_request" or "invalid_desired" or "unsupported_version" or "frame_too_large" =>
            new InvalidDataException($"The simulator rejected the request: {error}."),
        "busy" => new InvalidOperationException("The simulator is busy with another motion operation."),
        "disconnected" => new IOException("The simulator is disconnected."),
        _ => new IOException("The simulator rejected the request for an unknown reason."),
    };

}
