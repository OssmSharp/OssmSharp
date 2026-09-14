using OSSMDotNet.Web.ControlCore;
using OSSMDotNet.Simulation;
using OSSMDotNet.Web.HostRuntime;
using Microsoft.Extensions.Options;

namespace OSSMDotNet.Web.AdminService;

public sealed class AdminOperations(
    OperatorCapability capability,
    PrototypeEngine engine,
    SimulatorProcessBridge bridge,
    IOptions<AdminOptions> options,
    OperatorLogProvider activity,
    ILogger<AdminOperations> logger)
{
    public ControlSnapshot Read(string token) { capability.Demand(token); return engine.Snapshot; }
    public IReadOnlyList<OperatorLogEntry> ReadLog(string token, long after) { capability.Demand(token); return activity.ReadAfter(after); }
    public Invitation Generate(string token, Guid attempt)
    {
        capability.Demand(token);
        var invitation = engine.CreateInvitation(attempt, new Uri(options.Value.GuestOrigin), capability.ScopeId);
        Log("Invitation ready");
        return invitation;
    }
    public void Approve(string token, string connection) { capability.Demand(token); engine.Approve(connection); Log("Guest approved"); }
    public void Limits(string token, int cap, int depth) { capability.Demand(token); engine.SetLimits(new(cap, depth)); Log("Session limits applied; control paused"); }
    public void Recover(string token) { capability.Demand(token); engine.Recover(); Log("Connection ready; control paused"); }
    public void Arm(string token) { capability.Demand(token); engine.Arm(); Log("Control enabled"); }
    public void Stop(string token) { capability.Demand(token); engine.Pause(); Log("Stop requested"); }
    public void End(string token) { capability.Demand(token); engine.EndSession(); Log("Session ended"); }
    public void Unlock(string token, long revision) { capability.Demand(token); engine.Unlock(revision); Log("Unlocked; control remains paused"); }

    private void Log(string action) => logger.LogInformation(new EventId(200, "OperatorActivity"), "{Action}.", action);
    public Task Fault(string token, string kind)
    {
        capability.Demand(token);
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
