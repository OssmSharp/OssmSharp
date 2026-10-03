using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OssmSharp.Runtime.Oem.Protocol;

namespace OssmSharp.Runtime.Oem.Tests;

internal sealed class TestDevice(ILogger<DeviceBase>? logger = null) : DeviceBase(logger ?? NullLogger<DeviceBase>.Instance)
{
    public List<DeviceCommand> Writes { get; } = [];

    public DeviceState LiveState { get; set; } = new(new MotionSettings(0, 0, 0, 0, 0));

    public Func<DeviceCommand, CancellationToken, Task>? Write { get; set; }

    public Func<CancellationToken, Task<DeviceState>>? Read { get; set; }

    public Func<CancellationToken, Task>? Connect { get; set; }

    public Func<CancellationToken, Task<bool>>? Prepare { get; set; }

    public Func<Task>? Disconnect { get; set; }

    public int Connects { get; private set; }

    public int Prepares { get; private set; }

    public int Reads { get; private set; }

    public int Disconnects { get; private set; }

    public void Observe(MotionSettings settings) => UpdateState(new(settings));

    public void Fail(Exception exception) => SetFaulted(exception);

    protected override Task ConnectCoreAsync(CancellationToken cancellationToken)
    {
        Connects++;
        return Connect?.Invoke(cancellationToken) ?? Task.CompletedTask;
    }

    protected override Task<bool> PreparePatternAsync(CancellationToken cancellationToken)
    {
        Prepares++;
        return Prepare?.Invoke(cancellationToken) ?? Task.FromResult(true);
    }

    protected override Task<DeviceState> ReadStateAsync(CancellationToken cancellationToken)
    {
        Reads++;
        return Read?.Invoke(cancellationToken) ?? Task.FromResult(LiveState);
    }

    protected override Task DisconnectCoreAsync()
    {
        Disconnects++;
        return Disconnect?.Invoke() ?? Task.CompletedTask;
    }

    private protected override Task WriteCommandAsync(DeviceCommand command, CancellationToken cancellationToken)
    {
        Writes.Add(command);
        return Write?.Invoke(command, cancellationToken) ?? Task.CompletedTask;
    }

}
