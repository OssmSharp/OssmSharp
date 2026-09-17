using Microsoft.Extensions.Options;
using OSSMDotNet.ControlService.Models;
using OSSMDotNet.DeviceBridge;

namespace OSSMDotNet.ControlService;

/// <summary>Owns the serialized device bridge lifecycle and feedback loop.</summary>
public sealed class DeviceLoop(
    ControlAuthority authority,
    SimulatorProcessBridge bridge,
    ControlPushDispatcher dispatcher,
    IOptions<ControlServiceOptions> options,
    TimeProvider time,
    ILogger<DeviceLoop> logger) : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        bridge.Start(options.Value.SimulatorPath);
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
                    ControlServiceLog.DeviceSafetyState(logger,
                        mode == "Locked" ? "External changes detected; controls locked" : "Control fault detected; device commands stopped");
                lastMode = mode;
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    /// <summary>Pauses control and stops the device bridge during host shutdown.</summary>
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
