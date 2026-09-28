using Microsoft.Extensions.Logging.Abstractions;
using OssmSharp.Device.Primitives;

namespace OssmSharp.Device.Runtime.Tests;

internal sealed class TestDevice() : DeviceBase(NullLogger<DeviceBase>.Instance)
{
    public List<DeviceCommand> Writes { get; } = [];

    public DeviceState LiveState { get; set; } = new(new MotionSettings(0, 0, 0, 0, 0));

    public Func<DeviceCommand, CancellationToken, Task>? Write { get; set; }

    public Func<CancellationToken, Task<DeviceState>>? Read { get; set; }

    public Func<CancellationToken, Task>? Connect { get; set; }

    public Func<Task>? Disconnect { get; set; }

    public int Reads { get; private set; }

    public int Disconnects { get; private set; }

    public void Observe(MotionSettings settings) => UpdateState(new(settings));

    protected override Task ConnectCoreAsync(CancellationToken cancellationToken) => Connect?.Invoke(cancellationToken) ?? Task.CompletedTask;

    protected override Task WriteCommandAsync(DeviceCommand command, CancellationToken cancellationToken)
    {
        Writes.Add(command);
        return Write?.Invoke(command, cancellationToken) ?? Task.CompletedTask;
    }

    protected override Task<DeviceState> ReadStateAsync(CancellationToken cancellationToken)
    {
        Reads++;
        return Read?.Invoke(cancellationToken) ?? Task.FromResult(LiveState);
    }

    protected override Task DisconnectAsync()
    {
        Disconnects++;
        return Disconnect?.Invoke() ?? Task.CompletedTask;
    }
}
