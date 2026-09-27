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

    public bool Disposed { get; private set; }

    public int Writes { get; private set; }

    public Action? OnDispose { get; set; }

    public DeviceState? InitialReport { get; set; }

    public TaskCompletionSource? ExitGate { get; init; }

    public TaskCompletionSource? MotionWriteGate { get; init; }

    public int MotionWriteGateOnWrite { get; init; } = 1;

    public bool FailDispose { get; set; }

    public Task SetMotionAsync(MotionSettings settings, CancellationToken cancellationToken)
    {
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

    public void Report(DeviceState state) => DeviceStateChanged?.Invoke(state);

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
        ConnectionStateChanged?.Invoke(ConnectionState);
    }
}
