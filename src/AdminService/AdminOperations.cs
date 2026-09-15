using OSSMDotNet.Web.ControlCore;
using OSSMDotNet.Simulation;
using OSSMDotNet.Web.HostRuntime;
using Microsoft.Extensions.Options;

namespace OSSMDotNet.Web.AdminService;

public sealed class AdminOperations(
    PrototypeEngine engine,
    SimulatorProcessBridge bridge,
    IOptions<AdminOptions> options,
    OperatorLogProvider activity,
    ILogger<AdminOperations> logger)
{
    public ControlSnapshot Read() => engine.Snapshot;
    public IReadOnlyList<OperatorLogEntry> ReadLog(long after) => activity.ReadAfter(after);
    public Invitation ReadInvitation() => engine.GetInvitation(new Uri(options.Value.GuestOrigin));
    public Invitation CycleInvitation()
    {
        var invitation = engine.CycleInvitation(new Uri(options.Value.GuestOrigin));
        Log("Invitation cycled; guest revoked and Stop requested");
        return invitation;
    }
    public Invitation Generate(Guid attempt)
    {
        var invitation = engine.CreateInvitation(attempt, new Uri(options.Value.GuestOrigin), options.Value.PipeName);
        Log("Invitation ready");
        return invitation;
    }
    public void Approve(string connection) { engine.Approve(connection); Log("Guest approved"); }
    public void Limits(int cap, int depth) { engine.SetLimits(new(cap, depth)); Log("Session limits applied"); }
    public void Recover() { engine.Recover(); Log("Connection ready; control paused"); }
    public void Arm() { engine.Arm(); Log("Control enabled"); }
    public void Stop() { engine.Pause(); Log("Stop requested"); }
    public void End()
    {
        engine.CycleInvitation(new Uri(options.Value.GuestOrigin));
        Log("Stop requested; session ended and invitation rotated");
    }
    public void Unlock(long revision) { engine.Unlock(revision); Log("Unlocked; control remains paused"); }

    private void Log(string action) => logger.LogInformation(new EventId(200, "OperatorActivity"), "{Action}.", action);
    public Task Fault(string kind)
    {
        return kind switch
        {
            "external-zero" => bridge.InjectAsync("external", engine.Snapshot.Desired with { Speed = 0 }),
            "external-depth" => bridge.InjectAsync("external", engine.Snapshot.Desired with { Depth = Math.Min(100, engine.Snapshot.Desired.Depth + 1) }),
            "delay" => bridge.InjectAsync("delay", value: 1800),
            "stale" => bridge.InjectAsync("stale", value: 1),
            "fresh" => bridge.InjectAsync("stale", value: 0),
            "disconnect" => bridge.InjectAsync("disconnect"),
            "connect" => bridge.InjectAsync("connect"),
            _ => throw new ControlRejectedException("unknown-simulator-fault")
        };
    }
}
