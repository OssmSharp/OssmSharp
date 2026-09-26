using System.Buffers;
using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using OSSMDotNet.Simulator.Models;
using OssmSharp.Device.Runtime;

namespace OSSMDotNet.Simulator;

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
    private readonly SimulatedDeviceOptions _options;
    private readonly ILogger<SimulatedDevice> _logger;
    private readonly TimeProvider _timeProvider;
    private Process? _process;
    private long _requestId;
    private long _lastSampleSequence;
    private bool _hasDiagnostics;

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
        : base(options, timeProvider, logger)
    {
        _options = options;
        _logger = logger;
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    protected override async Task ConnectAsync(CancellationToken cancellationToken)
    {
        ValidateExecutable();
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(_options.ExecutablePath)
            {
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(_options.ExecutablePath),
            },
        };

        try
        {
            if (!process.Start())
            {
                throw new IOException("The simulator process could not be started.");
            }
        }
        catch (Exception exception) when (exception is not IOException and not UnauthorizedAccessException)
        {
            throw new IOException("The simulator process could not be started.", exception);
        }

        using var processJob = WindowsProcessJob.CreateFor(process);
        _process = process;
        _logger.Started();
        var reader = ReadOutputAsync(process.StandardOutput.BaseStream, cancellationToken);
        var diagnostics = DrainDiagnosticsAsync(process.StandardError, cancellationToken);
        try
        {
            await SendAsync("status", null, cancellationToken).ConfigureAwait(false);
            var processExit = process.WaitForExitAsync(cancellationToken);
            var completed = await Task.WhenAny(processExit, reader).ConfigureAwait(false);
            await completed.ConfigureAwait(false);
            if (ReferenceEquals(completed, processExit))
            {
                await reader.ConfigureAwait(false);
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                throw new IOException($"The simulator process exited with code {process.ExitCode}.");
            }
        }
        finally
        {
            process.StandardInput.Close();
            if (!process.HasExited)
            {
                using var shutdownTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try
                {
                    await process.WaitForExitAsync(shutdownTimeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (shutdownTimeout.IsCancellationRequested)
                {
                    _logger.ShutdownTimedOut();
                    process.Kill(entireProcessTree: true);
                    using var terminationTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await process.WaitForExitAsync(terminationTimeout.Token).ConfigureAwait(false);
                }
            }

            CompletePending(new OperationCanceledException("The simulator is stopping.", cancellationToken));
            await IgnoreShutdownCancellationAsync(reader, cancellationToken).ConfigureAwait(false);
            await IgnoreShutdownCancellationAsync(diagnostics, cancellationToken).ConfigureAwait(false);
            _process = null;
            PublishDisconnected();
            _logger.Stopped();
        }
    }

    /// <inheritdoc />
    protected override Task DispatchMotionAsync(
        MotionSettings motionSettings,
        CancellationToken cancellationToken)
    {
        if (motionSettings.Pattern != MotionPattern.SimpleStroke)
        {
            throw new NotSupportedException("The simulator currently supports only Simple Stroke.");
        }

        if (motionSettings.Sensation != 50)
        {
            throw new NotSupportedException("The simulator requires sensation 50 for ordinary writes.");
        }

        if (motionSettings.Buffer != 0)
        {
            throw new NotSupportedException("The simulator supports buffer 0 only.");
        }

        if (motionSettings.Speed == 0)
        {
            return SendAsync("stop", null, cancellationToken);
        }

        var desired = new SimulatorDesired(
            (int)motionSettings.Pattern,
            motionSettings.Speed,
            motionSettings.Stroke,
            motionSettings.Depth,
            motionSettings.Sensation);
        return SendAsync("write", desired, cancellationToken);
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
        await SendAsync("disconnect", null, cancellationToken).ConfigureAwait(false);
        PublishDisconnected();
    }

    /// <summary>
    /// Reconnects the simulator for integration testing.
    /// </summary>
    /// <returns>Completion after the simulator reconnects.</returns>
    internal async Task ReconnectAsync(CancellationToken cancellationToken)
    {
        await SendAsync("connect", null, cancellationToken).ConfigureAwait(false);
        PublishConnected(Information);
    }

    /// <inheritdoc />
    protected override async ValueTask DisposeCoreAsync()
    {
        _process?.Dispose();
        _inputGate.Dispose();
        await base.DisposeCoreAsync().ConfigureAwait(false);
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
        var process = _process;
        if (process is null || process.HasExited)
        {
            throw new IOException("The simulator is unavailable.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        long id;
        TaskCompletionSource<SimulatorReply> completion;
        lock (_admissionGate)
        {
            if (_pending.Count >= MaximumPendingRequests)
            {
                throw new InvalidOperationException("The simulator request capacity is exhausted.");
            }

            id = Interlocked.Increment(ref _requestId);
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_pending.TryAdd(id, completion))
            {
                throw new InvalidOperationException("A duplicate simulator request identifier was generated.");
            }
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
                await process.StandardInput.BaseStream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await process.StandardInput.BaseStream.WriteAsync(NewLine, cancellationToken).ConfigureAwait(false);
                await process.StandardInput.BaseStream.FlushAsync(cancellationToken).ConfigureAwait(false);
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

        if (reply.Ok is not false &&
            reply.State is { } state &&
            state.SampleSequence > _lastSampleSequence)
        {
            _lastSampleSequence = state.SampleSequence;

            if (state.Connected)
            {
                PublishConnected(Information);
            }
            else
            {
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
    }

    private async Task DrainDiagnosticsAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        using var owner = MemoryPool<char>.Shared.Rent(1024);
        while (await reader.ReadAsync(owner.Memory, cancellationToken).ConfigureAwait(false) > 0)
        {
            if (!_hasDiagnostics)
            {
                _hasDiagnostics = true;
                _logger.DiagnosticsAvailable();
            }
        }
    }

    private void CompletePending(Exception exception)
    {
        foreach (var pair in _pending)
        {
            if (_pending.TryRemove(pair.Key, out var completion))
            {
                completion.TrySetException(exception);
            }
        }
    }

    private void ValidateExecutable()
    {
        if (!Path.IsPathFullyQualified(_options.ExecutablePath))
        {
            throw new ArgumentException("The simulator executable path must be absolute.", nameof(_options));
        }

        if (!File.Exists(_options.ExecutablePath))
        {
            throw new FileNotFoundException(
                "Build the ossm-sim-bridge executable before starting the simulator.",
                _options.ExecutablePath);
        }
    }

    private static SimulatedDeviceOptions ValidateOptions(IOptions<SimulatedDeviceOptions>? options)
    {
        return options?.Value ?? throw new ArgumentNullException(nameof(options));
    }

    private static Exception MapRejection(string? error) => error switch
    {
        "invalid_request" or "invalid_desired" or "unsupported_version" or "frame_too_large" =>
            new InvalidDataException($"The simulator rejected the request: {error}."),
        "busy" => new InvalidOperationException("The simulator is busy with another motion operation."),
        "disconnected" => new IOException("The simulator is disconnected."),
        _ => new IOException("The simulator rejected the request for an unknown reason."),
    };

    private static async Task IgnoreShutdownCancellationAsync(Task task, CancellationToken cancellationToken)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (IOException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }
}
