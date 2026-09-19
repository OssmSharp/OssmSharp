using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using OSSMDotNet.DeviceBridge.Models;

namespace OSSMDotNet;

/// <summary>
/// Runs the simulator adapter and serializes its bounded device command protocol.
/// </summary>
public sealed class SimulatedDevice(IOptions<SimulatedDeviceOptions> options, ILogger<SimulatedDevice> log)
    : BackgroundService, IOssmDevice
{
    private SimulatedDeviceOptions _options = options.Value;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        MaxDepth = 12
    };

    private readonly ConcurrentDictionary<long, TaskCompletionSource<SimulatorReply>> _pending = new();
    private readonly Lock _admissionGate = new();
    private readonly SemaphoreSlim _inputLock = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private Process? _process;
    private Task? _reader;
    private Task? _diagnostics;
    private DeviceState? _lastSample;
    private long _requestId;
    private bool _disposed;

    public event Action<DeviceState>? DeviceStateReceived;

    public string? Failure { get; private set; }
    public bool HasDiagnostics { get; private set; }

    /// <summary>
    /// Starts the configured simulator adapter process.
    /// </summary>
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _process = new Process
        {
            StartInfo = new ProcessStartInfo(_options.executablePath)
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
        {
            throw new IOException("simulator-start-failed");
        }

        LogSimulatorStarted(log);
        _reader = ReadAsync(_process.StandardOutput.BaseStream, _lifetime.Token);
        _diagnostics = DrainDiagnosticsAsync(_process.StandardError, _lifetime.Token);
        return Task.CompletedTask;
    }


    public Task SetMotionAsync(MotionSettings motionSettings, CancellationToken cancellationToken)
        => SendAsync(new SimulatorRequest(1, 0, "write", commandId, desired, null), cancellationToken);

    private async Task SendAsync(SimulatorRequest request, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (_process is null || Failure is not null)
        {
            throw new IOException("simulator-unavailable");
        }

        long id;
        TaskCompletionSource<SimulatorReply> completion;
        lock (_admissionGate)
        {
            if (_pending.Count >= 8)
            {
                throw new IOException("simulator-request-capacity");
            }

            id = Interlocked.Increment(ref _requestId);
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_pending.TryAdd(id, completion))
            {
                throw new InvalidOperationException("duplicate-simulator-request");
            }
        }
        try
        {
            var buffer = new ArrayBufferWriter<byte>(256);
            using (var writer = new Utf8JsonWriter(buffer))
            {
                JsonSerializer.Serialize(writer, request with { Id = id }, Json);
            }

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
        {
            throw new IOException($"simulator-rejected:{response.Error ?? "unknown"}");
        }
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
                {
                    throw new IOException("simulator-exited");
                }

                used += read;
                var consumed = ConsumeLines(buffer.Span[..used]);
                buffer.Span[consumed..used].CopyTo(buffer.Span);
                used -= consumed;
                if (used == buffer.Length)
                {
                    throw new InvalidDataException("simulator-frame-too-large");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or JsonException or InvalidDataException)
        {
            Failure = "simulator-connection-failed";
            LogDeviceConnectionLost(log, error);
            foreach (var pair in _pending)
            {
                if (_pending.TryRemove(pair.Key, out var request))
                {
                    request.TrySetException(new IOException(Failure));
                }
            }

            if (_lastSample is { } sample)
            {
                DeviceStateReceived?.Invoke(sample with { ObservationSequenceNumber = sample.ObservationSequenceNumber + 1, Connected = false });
            }
        }
    }

    private int ConsumeLines(ReadOnlySpan<byte> buffer)
    {
        var consumed = 0;
        while (buffer[consumed..].IndexOf((byte)'\n') is var offset && offset >= 0)
        {
            var line = buffer.Slice(consumed, offset);
            if (!line.IsEmpty && line[^1] == '\r')
            {
                line = line[..^1];
            }

            var reply = JsonSerializer.Deserialize<SimulatorReply?>(line, Json) ?? throw new InvalidDataException("simulator-empty-reply");
            if (reply.V != 1 || (reply.Event != "state" && reply.Id is null))
            {
                throw new InvalidDataException("simulator-invalid-envelope");
            }

            if (reply.State is { } state)
            {
                if (_lastSample is { } previous && state.ObservationSequenceNumber >= previous.ObservationSequenceNumber)
                {
                    if ((state.Enabled || state.Moving) != (previous.Enabled || previous.Moving))
                    {
                        LogDeviceMotionState(log, state.Enabled || state.Moving ? "running" : "stopped");
                    }

                    if (state.Desired != previous.Desired)
                    {
                        LogDeviceSettingsUpdated(log);
                    }
                }
                _lastSample = state;
                SampleReceived?.Invoke(state);
            }
            if (reply.Id is { } id && _pending.TryRemove(id, out var completion))
            {
                completion.TrySetResult(reply);
            }

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
                {
                    LogDeviceDiagnosticsAvailable(log);
                }

                HasDiagnostics = true;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (IOException error)
        {
            Failure ??= "simulator-diagnostic-stream-failed";
            LogDeviceDiagnosticsDisconnected(log, error);
        }
    }

    /// <summary>
    /// Stops the adapter process and releases pending command resources.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

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
                        LogSimulatorShutdownTimedOut(log);
                        if (!process.HasExited)
                        {
                            process.Kill(entireProcessTree: true);
                        }

                        await process.WaitForExitAsync();
                    }
                    if (_reader is not null)
                    {
                        await _reader;
                    }

                    if (_diagnostics is not null)
                    {
                        await _diagnostics;
                    }

                    LogSimulatorStopped(log);
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
            {
                if (_pending.TryRemove(pair.Key, out var pending))
                {
                    pending.TrySetException(new ObjectDisposedException(nameof(SimulatorProcessBridge)));
                }
            }

            _lifetime.Dispose();
            _inputLock.Dispose();
        }
    }

}
