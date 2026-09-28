using Microsoft.Extensions.Hosting;
using OneOf;
using OssmSharp.Device.Primitives;
using OssmSharp.Device.Primitives.Models;

namespace OssmSharp.Cli.Tests.Helpers;

internal sealed class ReadOnlyTestDevice(
    bool connects = true,
    DeviceInformation? information = null,
    bool acceptStopWrites = false)
    : BackgroundService, IOssmDevice
{
    public event Action<DeviceConnectionState>? ConnectionStateChanged;

    public event Action<OneOf<DeviceState, Exception>>? DeviceStateChanged;

    public DeviceConnectionState ConnectionState { get; private set; }

    public DeviceInformation Device { get; } =
        information ?? new("enrolled-device", "stock", "1.0.34", [MotionPattern.SimpleStroke]);

    public DeviceState? LatestState { get; private set; }

    public bool IsReady { get; private set; }

    public bool Disposed { get; private set; }

    public int Writes { get; private set; }

    public Action? OnDispose { get; set; }

    public DeviceState? InitialReport { get; set; }

    public TaskCompletionSource? ExitGate { get; init; }

    public TaskCompletionSource? MotionWriteGate { get; init; }

    public int MotionWriteGateOnWrite { get; init; } = 1;

    public bool FailDispose { get; set; }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (ConnectionState != DeviceConnectionState.Connected
            || LatestState?.MotionSettings is not { Speed: 0, State: MotionDeviceState.StrokeEngineIdle } stopped)
        {
            throw new InvalidOperationException("The device must report stopped play-ready feedback.");
        }

        await WriteAsync(stopped with { Speed = 0 }, cancellationToken);
        IsReady = true;
    }

    public Task SetAsync(MotionSettings settings, CancellationToken cancellationToken) =>
        IsReady
            ? WriteAsync(settings, cancellationToken)
            : Task.FromException(new InvalidOperationException("Initialize the device before moving."));

    public Task EmergencyStopAsync(CancellationToken cancellationToken)
    {
        IsReady = false;
        return ConnectionState == DeviceConnectionState.Connected
            ? WriteAsync(LatestState?.MotionSettings is { } settings
                ? settings with { Speed = 0 }
                : new MotionSettings(MotionDeviceState.StrokeEngineIdle, 0, 20, 50, 50, 0, MotionPattern.SimpleStroke, 0),
                cancellationToken)
            : Task.FromException(new IOException("The device is disconnected."));
    }

    private Task WriteAsync(MotionSettings settings, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Writes++;
        if (settings.Speed == 0
            && Writes == MotionWriteGateOnWrite
            && MotionWriteGate is { } gate)
            return gate.Task;

        if (acceptStopWrites && settings.Speed == 0)
            return Task.CompletedTask;

        throw new NotSupportedException();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None);
        if (FailDispose)
            throw new IOException("Connection cleanup did not settle.");

        Disposed = true;
        OnDispose?.Invoke();
        Dispose();
    }

    public void Report(DeviceState state)
    {
        LatestState = state;
        DeviceStateChanged?.Invoke(state);
    }

    public void ReportFault(Exception exception)
    {
        IsReady = false;
        DeviceStateChanged?.Invoke(exception);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        ConnectionState = DeviceConnectionState.Connecting;
        ConnectionStateChanged?.Invoke(ConnectionState);
        ConnectionState = connects ? DeviceConnectionState.Connected : DeviceConnectionState.Disconnected;
        ConnectionStateChanged?.Invoke(ConnectionState);
        if (connects)
        {
            if (InitialReport is { } report)
                Report(report);
        }

        if (connects)
        {
            try
            {
                if (ExitGate is { } gate)
                    await gate.Task.WaitAsync(stoppingToken);
                else
                    await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
            }
        }

        ConnectionState = DeviceConnectionState.Disconnected;
        IsReady = false;
        ConnectionStateChanged?.Invoke(ConnectionState);
    }
}
