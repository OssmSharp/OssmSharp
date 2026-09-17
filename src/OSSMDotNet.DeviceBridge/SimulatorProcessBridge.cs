using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using OSSMDotNet.Models;

namespace OSSMDotNet.DeviceBridge;

/// <summary>Runs the simulator adapter and serializes its bounded device command protocol.</summary>
public sealed partial class SimulatorProcessBridge(ILogger<SimulatorProcessBridge> logger) : IDeviceBridge
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        MaxDepth = 12
    };
    private readonly ConcurrentDictionary<long, TaskCompletionSource<SimulatorReply>> _pending = new();
    private readonly SemaphoreSlim _inputLock = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private Process? _process;
    private Task? _reader;
    private Task? _diagnostics;
    private DeviceSample? _lastSample;
    private long _requestId;
    private int _disposed;

    public event Action<DeviceSample>? SampleReceived;
    public string? Failure { get; private set; }
    public bool HasDiagnostics { get; private set; }

    /// <summary>Starts the configured simulator adapter process.</summary>
    public void Start(string executable)
    {
        if (_process is not null)
            throw new InvalidOperationException("simulator-already-started");
        if (!File.Exists(executable))
            throw new FileNotFoundException("Build the ossm-sim-bridge executable before starting the prototype.");
        _process = new Process
        {
            StartInfo = new ProcessStartInfo(Path.GetFullPath(executable))
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            },
            EnableRaisingEvents = true
        };
        if (!_process.Start())
            throw new IOException("simulator-start-failed");
        LogSimulatorStarted(logger);
        _reader = ReadAsync(_process.StandardOutput.BaseStream, _lifetime.Token);
        _diagnostics = DrainDiagnosticsAsync(_process.StandardError, _lifetime.Token);
    }

    /// <summary>Writes requested device settings through the simulator adapter.</summary>
    public async Task WriteAsync(long commandId, DesiredState desired, CancellationToken cancellationToken) =>
        await SendAsync(new SimulatorRequest(1, 0, "write", commandId, desired, null), cancellationToken);

    /// <summary>Requests a stop through the simulator adapter.</summary>
    public async Task StopAsync(long commandId, CancellationToken cancellationToken) =>
        await SendAsync(new SimulatorRequest(1, 0, "stop", commandId, null, null), cancellationToken);

    /// <summary>Injects a simulator-only test operation.</summary>
    public Task InjectAsync(string operation, DesiredState? desired = null, int? value = null)
    {
        if (operation is not ("external" or "delay" or "stale" or "disconnect" or "connect"))
            throw new ArgumentException("unknown-simulator-fault", nameof(operation));
        return SendAsync(new SimulatorRequest(1, 0, operation, null, desired, value), _lifetime.Token);
    }

    private async Task SendAsync(SimulatorRequest request, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (_process is null || Failure is not null)
            throw new IOException("simulator-unavailable");
        if (_pending.Count >= 8)
            throw new IOException("simulator-request-capacity");
        var id = Interlocked.Increment(ref _requestId);
        var completion = new TaskCompletionSource<SimulatorReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(id, completion))
            throw new InvalidOperationException("duplicate-simulator-request");
        try
        {
            var buffer = new ArrayBufferWriter<byte>(256);
            using (var writer = new Utf8JsonWriter(buffer))
                JsonSerializer.Serialize(writer, request with { Id = id }, Json);
            buffer.GetSpan(1)[0] = (byte)'\n';
            buffer.Advance(1);
            await _inputLock.WaitAsync(cancellationToken);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                await _process.StandardInput.BaseStream.WriteAsync(buffer.WrittenMemory, _lifetime.Token);
                await _process.StandardInput.BaseStream.FlushAsync(_lifetime.Token);
            }
            finally
            {
                _inputLock.Release();
            }
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException)
        {
            _pending.TryRemove(id, out _);
            throw;
        }

        // After dispatch, completion belongs to the simulator, not the caller's cancellation token.
        var response = await completion.Task;
        if (response.Ok != true)
            throw new IOException($"simulator-rejected:{response.Error ?? "unknown"}");
    }

    private async Task ReadAsync(Stream output, CancellationToken cancellationToken)
    {
        using var owner = MemoryPool<byte>.Shared.Rent(16_384);
        var buffer = owner.Memory[..16_384];
        var used = 0;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var read = await output.ReadAsync(buffer[used..], cancellationToken);
                if (read == 0)
                    throw new IOException("simulator-exited");
                used += read;
                var consumed = ConsumeLines(buffer.Span[..used]);
                buffer.Span[consumed..used].CopyTo(buffer.Span);
                used -= consumed;
                if (used == buffer.Length)
                    throw new InvalidDataException("simulator-frame-too-large");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or JsonException or InvalidDataException)
        {
            Failure = "simulator-connection-failed";
            LogDeviceConnectionLost(logger, error);
            foreach (var pair in _pending)
                if (_pending.TryRemove(pair.Key, out var request))
                    request.TrySetException(new IOException(Failure));
            if (_lastSample is { } sample)
                SampleReceived?.Invoke(sample with { SampleSequence = sample.SampleSequence + 1, Connected = false });
        }
    }

    private int ConsumeLines(ReadOnlySpan<byte> buffer)
    {
        var consumed = 0;
        while (buffer[consumed..].IndexOf((byte)'\n') is var offset && offset >= 0)
        {
            var line = buffer.Slice(consumed, offset);
            if (!line.IsEmpty && line[^1] == '\r')
                line = line[..^1];
            var reply = JsonSerializer.Deserialize<SimulatorReply?>(line, Json) ?? throw new InvalidDataException("simulator-empty-reply");
            if (reply.V != 1 || (reply.Event != "state" && reply.Id is null))
                throw new InvalidDataException("simulator-invalid-envelope");
            if (reply.State is { } state)
            {
                if (_lastSample is { } previous && state.SampleSequence >= previous.SampleSequence)
                {
                    if ((state.Enabled || state.Moving) != (previous.Enabled || previous.Moving))
                        LogDeviceMotionState(logger, state.Enabled || state.Moving ? "running" : "stopped");
                    if (state.Desired != previous.Desired)
                        LogDeviceSettingsUpdated(logger);
                }
                _lastSample = state;
                SampleReceived?.Invoke(state);
            }
            if (reply.Id is { } id && _pending.TryRemove(id, out var completion))
                completion.TrySetResult(reply);
            consumed += offset + 1;
        }
        return consumed;
    }

    private async Task DrainDiagnosticsAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        using var owner = MemoryPool<char>.Shared.Rent(1024);
        try
        {
            while (await reader.ReadAsync(owner.Memory, cancellationToken) > 0)
            {
                if (!HasDiagnostics)
                    LogDeviceDiagnosticsAvailable(logger);
                HasDiagnostics = true;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (IOException error)
        {
            Failure ??= "simulator-diagnostic-stream-failed";
            LogDeviceDiagnosticsDisconnected(logger, error);
        }
    }

    [LoggerMessage(EventId = 100, EventName = "SimulatorStarted", Level = LogLevel.Information, Message = "Simulator started.")]
    private static partial void LogSimulatorStarted(ILogger logger);

    [LoggerMessage(EventId = 101, EventName = "DeviceConnectionLost", Level = LogLevel.Error,
        Message = "Device connection lost; feedback is unavailable.")]
    private static partial void LogDeviceConnectionLost(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 102, EventName = "DeviceMotionState", Level = LogLevel.Information, Message = "Device {MotionState}.")]
    private static partial void LogDeviceMotionState(ILogger logger, string motionState);

    [LoggerMessage(EventId = 103, EventName = "DeviceSettingsUpdated", Level = LogLevel.Information, Message = "Device settings updated.")]
    private static partial void LogDeviceSettingsUpdated(ILogger logger);

    [LoggerMessage(EventId = 104, EventName = "DeviceDiagnosticsAvailable", Level = LogLevel.Warning, Message = "Device diagnostics are available.")]
    private static partial void LogDeviceDiagnosticsAvailable(ILogger logger);

    [LoggerMessage(EventId = 105, EventName = "DeviceDiagnosticsDisconnected", Level = LogLevel.Error, Message = "Device diagnostics disconnected.")]
    private static partial void LogDeviceDiagnosticsDisconnected(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 106, EventName = "SimulatorShutdownTimedOut", Level = LogLevel.Warning,
        Message = "Simulator shutdown timed out; stopping it.")]
    private static partial void LogSimulatorShutdownTimedOut(ILogger logger);

    [LoggerMessage(EventId = 107, EventName = "SimulatorStopped", Level = LogLevel.Information, Message = "Simulator stopped.")]
    private static partial void LogSimulatorStopped(ILogger logger);

    /// <summary>Stops the adapter process and releases pending command resources.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        try
        {
            await _lifetime.CancelAsync();
            if (_process is { } process)
            {
                try
                {
                    process.StandardInput.Close();
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    try { await process.WaitForExitAsync(timeout.Token); }
                    catch (OperationCanceledException)
                    {
                        LogSimulatorShutdownTimedOut(logger);
                        if (!process.HasExited) process.Kill(entireProcessTree: true);
                        await process.WaitForExitAsync();
                    }
                    if (_reader is not null) await _reader;
                    if (_diagnostics is not null) await _diagnostics;
                    LogSimulatorStopped(logger);
                }
                finally
                {
                    _process?.Dispose();
                    _process = null;
                }
            }
        }
        finally
        {
            foreach (var pair in _pending)
                if (_pending.TryRemove(pair.Key, out var pending))
                    pending.TrySetException(new ObjectDisposedException(nameof(SimulatorProcessBridge)));
            _lifetime.Dispose();
            _inputLock.Dispose();
        }
    }
}
