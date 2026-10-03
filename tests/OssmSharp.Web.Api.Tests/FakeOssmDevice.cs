using OssmSharp.Runtime.Oem;

namespace OssmSharp.Web.Api.Tests;

internal sealed class FakeOssmDevice : IOssmDevice
{
    public event EventHandler? StateChanged
    {
        add { }
        remove { }
    }

    public bool IsArmed { get; set; } = true;

    public bool IsConnected { get; set; } = true;

    public string DeviceId { get; } = "test-device";

    public string? Name { get; set; }

    public Exception? ConnectionFailure { get; set; }

    public DeviceState? State { get; set; }

    public SetResult SetResult { get; set; } = new(SetStatus.Completed, 5);

    public TaskCompletionSource<SetResult>? PendingSet { get; set; }

    public TaskCompletionSource? PendingConnect { get; set; }

    public Exception? StopException { get; set; }

    public TaskCompletionSource? PendingStop { get; set; }

    public TaskCompletionSource? PendingReset { get; set; }

    public Exception? ResetException { get; set; }

    public MotionSettings? LastSettings { get; private set; }

    public int Connects { get; private set; }

    public int Disconnects { get; private set; }

    public int Sets { get; private set; }

    public int Stops { get; private set; }

    public int Resets { get; private set; }

    public int Disposals { get; private set; }

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Connects++;
        return PendingConnect?.Task.WaitAsync(cancellationToken) ?? Task.CompletedTask;
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Disconnects++;
        return Task.CompletedTask;
    }

    public Task<SetResult> SetAsync(MotionSettings settings, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Sets++;
        LastSettings = settings;
        return PendingSet?.Task ?? Task.FromResult(SetResult);
    }

    public Task<DeviceState?> GetStateAsync(
        bool bypassCache = false,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(State);
    }

    public Task EmergencyStopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Stops++;
        if (PendingConnect is { Task.IsCompleted: false })
        {
            PendingConnect.TrySetCanceled(CancellationToken.None);
            return Task.CompletedTask;
        }

        if (StopException is not null)
        {
            throw StopException;
        }

        return PendingStop?.Task ?? Task.CompletedTask;
    }

    public Task ResetAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Resets++;
        if (ResetException is not null)
        {
            throw ResetException;
        }

        return PendingReset?.Task.WaitAsync(cancellationToken) ?? Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Disposals++;
        return ValueTask.CompletedTask;
    }
}
