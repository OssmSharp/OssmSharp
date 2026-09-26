namespace OssmSharp.Web.Api.Device;

/// <summary>
/// Owns the serialized device feedback loop.
/// </summary>
/// <param name="authority">The owner of device and control state.</param>
/// <param name="dispatcher">The publisher notified of state changes.</param>
/// <param name="time">The clock used for periodic device polling.</param>
/// <param name="logger">The logger for loop failures.</param>
public sealed class DeviceLoop(
    ControlAuthority authority,
    ControlPushDispatcher dispatcher,
    TimeProvider time,
    ILogger<DeviceLoop> logger) : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(50), time);
        string? lastMode = null;
        try
        {
            do
            {
                authority.Tick();
                dispatcher.Trigger();
                var mode = authority.Read().Snapshot.Mode;
                if (mode != lastMode && mode is "Locked" or "Faulted")
                    ApiLog.DeviceSafetyState(logger,
                        mode == "Locked" ? "External changes detected; controls locked" : "Control fault detected; device commands stopped");
                lastMode = mode;
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    /// <summary>
    /// Pauses control before the hosted device stops during host shutdown.
    /// </summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        authority.StopForShutdown();
        var start = time.GetTimestamp();
        while (time.GetElapsedTime(start) < TimeSpan.FromSeconds(2) && !cancellationToken.IsCancellationRequested)
        {
            authority.Tick();
            await Task.Delay(50, cancellationToken);
        }
        await base.StopAsync(cancellationToken);
    }
}
