using System.IO.Pipes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OSSMDotNet.Simulator;
using OSSMDotNet.Simulator.Models;

namespace OSSMDotNet.DeviceCli.Simulator;

/// <summary>
/// Serializes simulator commands and guarantees zero-speed settlement before shutdown.
/// </summary>
internal sealed class SimulatorHostSession : IAsyncDisposable
{
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(5);
    private readonly SimulatedDevice _device;
    private readonly SimulatorEndpoint _endpoint;
    private readonly string _simulatorPath;
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly TaskCompletionSource<DeviceState> _firstState =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<Exception> _backgroundFailure =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenSource? _hostCancellation;
    private CancellationTokenSource? _movementCancellation;
    private Task _movementStop = Task.CompletedTask;
    private DeviceState? _latestState;
    private long _movementGeneration;
    private bool _connectionStarted;
    private bool _stopping;

    /// <summary>
    /// Initializes a simulator session for one native executable and private endpoint.
    /// </summary>
    /// <param name="simulatorPath">The native simulator executable path.</param>
    /// <param name="endpoint">The endpoint to serve.</param>
    internal SimulatorHostSession(string simulatorPath, SimulatorEndpoint endpoint)
    {
        _simulatorPath = simulatorPath;
        _endpoint = endpoint;
        _device = new SimulatedDevice(
            Options.Create(new SimulatedDeviceOptions { ExecutablePath = simulatorPath }),
            TimeProvider.System,
            NullLogger<SimulatedDevice>.Instance);
        _device.ConnectionStateChanged += OnConnectionStateChanged;
        _device.DeviceStateChanged += OnDeviceStateChanged;
    }

    /// <summary>
    /// Releases the simulator device and the session's cancellation and serialization resources.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        _serial.Dispose();
        _movementCancellation?.Dispose();
        _hostCancellation?.Dispose();
        await _device.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Starts the simulator and serves requests until a stop request or cancellation occurs.
    /// </summary>
    /// <param name="cancellationToken">The token that stops the host session.</param>
    /// <returns>The host exit code after settling at zero speed.</returns>
    internal async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        _hostCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await _device.StartAsync(cancellationToken).ConfigureAwait(false);
        await _firstState.Task.WaitAsync(OperationTimeout, cancellationToken).ConfigureAwait(false);

        try
        {
            await ServeAsync(_hostCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_hostCancellation.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
        finally
        {
            _stopping = true;
            await CancelMovementAsync().ConfigureAwait(false);
            var state = Volatile.Read(ref _latestState);
            if (state is not null && _device.ConnectionState == DeviceConnectionState.Connected)
            {
                await SettleStopAsync(state.MotionSettings).ConfigureAwait(false);
            }

            await _device.StopAsync(CancellationToken.None)
                .WaitAsync(OperationTimeout, CancellationToken.None)
                .ConfigureAwait(false);
        }

        if (_backgroundFailure.Task.IsCompletedSuccessfully)
        {
            throw new IOException(
                "The simulator failed while running the diagnostic host.",
                await _backgroundFailure.Task.ConfigureAwait(false));
        }

        return 0;
    }

    private async Task ServeAsync(CancellationToken cancellationToken)
    {
        var stopHost = false;
        while (!stopHost)
        {
            await using var pipe = new NamedPipeServerStream(
                _endpoint.PipeName,
                PipeDirection.InOut,
                maxNumberOfServerInstances: 1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);

            SimulatorIpcResponse response;
            try
            {
                var request = await SimulatorIpcProtocol.ReadAsync<SimulatorIpcRequest>(pipe, cancellationToken)
                    .ConfigureAwait(false);
                (response, stopHost) = await ProcessAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                response = new SimulatorIpcResponse(false, Error: exception.Message);
            }

            await SimulatorIpcProtocol.WriteAsync(pipe, response, CancellationToken.None)
                .WaitAsync(OperationTimeout, CancellationToken.None)
                .ConfigureAwait(false);
        }
    }

    private async Task<(SimulatorIpcResponse Response, bool StopHost)> ProcessAsync(
        SimulatorIpcRequest request,
        CancellationToken cancellationToken)
    {
        await _serial.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = Volatile.Read(ref _latestState)
                ?? throw new InvalidDataException("The simulator has not published device state.");
            switch (request.Operation)
            {
                case "start":
                    return PathsEqual(request.SimulatorPath, _simulatorPath)
                        ? (Success(state), false)
                        : (new SimulatorIpcResponse(
                            false,
                            Error: "A simulator host is already running with a different executable."), false);
                case "status":
                    return (Success(state), false);
                case "move":
                    var validationError = ValidateMove(request);
                    if (validationError is not null)
                    {
                        return (new SimulatorIpcResponse(false, Error: validationError), false);
                    }

                    return (await MoveAsync(request, state, cancellationToken).ConfigureAwait(false), false);
                case "stop":
                    await CancelMovementAsync().ConfigureAwait(false);
                    await SettleStopAsync(state.MotionSettings).ConfigureAwait(false);
                    state = Volatile.Read(ref _latestState) ?? state with
                    {
                        MotionSettings = state.MotionSettings with { Speed = 0 },
                    };
                    return (Success(state), true);
                default:
                    return (new SimulatorIpcResponse(false, Error: "Unknown simulator host operation."), false);
            }
        }
        finally
        {
            _serial.Release();
        }
    }

    private async Task<SimulatorIpcResponse> MoveAsync(
        SimulatorIpcRequest request,
        DeviceState state,
        CancellationToken cancellationToken)
    {
        var moving = state with
        {
            MotionSettings = state.MotionSettings with
            {
                State = MotionDeviceState.StrokeEngineIdle,
                Speed = request.Speed,
                Stroke = request.Stroke,
                Sensation = 50,
                Depth = request.Depth,
                Buffer = 0,
                Pattern = MotionPattern.SimpleStroke,
            },
        };
        await SetMotionAsync(moving.MotionSettings, cancellationToken).ConfigureAwait(false);
        moving = Volatile.Read(ref _latestState) ?? moving;

        await CancelMovementAsync().ConfigureAwait(false);
        _movementCancellation = CancellationTokenSource.CreateLinkedTokenSource(_hostCancellation!.Token);
        var generation = Interlocked.Increment(ref _movementGeneration);
        _movementStop = StopAfterAsync(
            TimeSpan.FromMilliseconds(request.DurationMilliseconds),
            generation,
            _movementCancellation.Token);
        return Success(moving);
    }

    private async Task StopAfterAsync(
        TimeSpan duration,
        long generation,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(duration, cancellationToken).ConfigureAwait(false);
            await _serial.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (generation == Volatile.Read(ref _movementGeneration))
                {
                    var state = Volatile.Read(ref _latestState);
                    if (state is not null)
                    {
                        await SettleStopAsync(state.MotionSettings).ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                _serial.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _backgroundFailure.TrySetResult(exception);
            await _hostCancellation!.CancelAsync().ConfigureAwait(false);
        }
    }

    private async Task CancelMovementAsync()
    {
        Interlocked.Increment(ref _movementGeneration);
        if (_movementCancellation is null)
        {
            return;
        }

        await _movementCancellation.CancelAsync().ConfigureAwait(false);
        await ObserveCancellationAsync(_movementStop).ConfigureAwait(false);
        _movementCancellation.Dispose();
        _movementCancellation = null;
        _movementStop = Task.CompletedTask;
    }

    private async Task SettleStopAsync(MotionSettings motionSettings)
    {
        await SetMotionAsync(motionSettings with { Speed = 0 }, CancellationToken.None)
            .ConfigureAwait(false);
    }

    private async Task SetMotionAsync(
        MotionSettings motionSettings,
        CancellationToken cancellationToken)
    {
        var operation = _device.SetMotionAsync(motionSettings, cancellationToken);
        try
        {
            await operation.WaitAsync(OperationTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException exception)
        {
            await _device.StopAsync(CancellationToken.None)
                .WaitAsync(OperationTimeout, CancellationToken.None)
                .ConfigureAwait(false);
            await ObserveCancellationAsync(operation).ConfigureAwait(false);
            throw new TimeoutException("The simulator did not acknowledge the motion command.", exception);
        }
    }

    private void OnConnectionStateChanged(DeviceConnectionState state)
    {
        if (state == DeviceConnectionState.Connecting)
        {
            _connectionStarted = true;
        }
        else if (state == DeviceConnectionState.Disconnected && _connectionStarted && !_stopping)
        {
            var exception = new IOException("The simulator stopped unexpectedly.");
            if (!_firstState.TrySetException(exception))
            {
                _backgroundFailure.TrySetResult(exception);
                _ = _hostCancellation?.CancelAsync();
            }
        }
    }

    private void OnDeviceStateChanged(OneOf.OneOf<DeviceState, Exception> change)
    {
        if (change.IsT0)
        {
            Volatile.Write(ref _latestState, change.AsT0);
            _firstState.TrySetResult(change.AsT0);
        }
        else
        {
            if (!_firstState.TrySetException(change.AsT1))
            {
                _backgroundFailure.TrySetResult(change.AsT1);
                _ = _hostCancellation?.CancelAsync();
            }
        }
    }

    private SimulatorIpcResponse Success(DeviceState state) =>
        new(true, new DeviceCommandOutput(_device.ConnectionState, _device.Device, state));

    private string? ValidateMove(SimulatorIpcRequest request)
    {
        if (request.Speed is < 1 or > 100 ||
            request.Stroke is < 1 or > 100 ||
            request.Depth is < 1 or > 100 ||
            request.Stroke > request.Depth ||
            request.DurationMilliseconds is < 100 or > 60_000)
        {
            return "The motion request is outside the supported bounds.";
        }

        return null;
    }

    private bool PathsEqual(string? first, string second) =>
        first is not null && string.Equals(
            Path.GetFullPath(first),
            Path.GetFullPath(second),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private async Task ObserveCancellationAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }
}
