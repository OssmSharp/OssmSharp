using OSSMDotNet.Web.ControlCore;
using OSSMDotNet.Simulation;
using OSSMDotNet.Web.HostRuntime;
using Microsoft.Extensions.Options;

namespace OSSMDotNet.Web.AdminService;

public sealed class DeviceLoop(PrototypeEngine engine, SimulatorProcessBridge bridge, IOptions<AdminOptions> options, TimeProvider time,
    ILogger<DeviceLoop> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        bridge.Start(options.Value.SimulatorPath);
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(50), time);
        string? lastMode = null;
        do
        {
            engine.Tick();
            var mode = engine.Snapshot.Mode;
            if (mode != lastMode && mode is "Locked" or "Faulted")
                logger.LogWarning(new EventId(201, "OperatorActivity"), "{Status}.",
                    mode == "Locked" ? "External changes detected; controls locked" : "Control fault; sending stopped");
            lastMode = mode;
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        engine.Pause("admin-stopping");
        var start = time.GetTimestamp();
        while (time.GetElapsedTime(start) < TimeSpan.FromSeconds(2) && !cancellationToken.IsCancellationRequested)
        {
            engine.Tick();
            await Task.Delay(50, cancellationToken);
        }
        await base.StopAsync(cancellationToken);
    }
}
