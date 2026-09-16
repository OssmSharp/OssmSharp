using OSSMDotNet.DeviceBridge;
using OSSMDotNet.Models;
using OSSMDotNet.ControlCore;
using OSSMDotNet.ServiceDefaults;
using Microsoft.Extensions.Options;

namespace OSSMDotNet.AdminService;

/// <summary>Coordinates operator operations through the admin-owned controller and device bridge.</summary>
public sealed class AdminOperations(
    PrototypeEngine engine,
    SimulatorProcessBridge bridge,
    IOptions<AdminOptions> options,
    OperatorLogProvider activity,
    ILogger<AdminOperations> logger)
{
    /// <summary>Returns the current trusted controller snapshot.</summary>
    public ControlSnapshot Read() => engine.Snapshot;

    /// <summary>Returns operator activity entries after a sequence number.</summary>
    public IReadOnlyList<OperatorLogEntry> ReadLog(long after) => activity.ReadAfter(after);

    /// <summary>Returns the current reusable invitation.</summary>
    public Invitation ReadInvitation() => engine.GetInvitation(new Uri(options.Value.GuestOrigin));

    /// <summary>Replaces the invitation and revokes any active guest authority.</summary>
    public Invitation CycleInvitation()
    {
        var invitation = engine.CycleInvitation(new Uri(options.Value.GuestOrigin));
        LogOperatorActivity("Invitation link replaced; guest access revoked and Stop requested");
        return invitation;
    }
    /// <summary>Retrieves the current invitation through the compatibility API.</summary>
    public Invitation Generate(Guid attempt)
    {
        var invitation = engine.CreateInvitation(attempt, new Uri(options.Value.GuestOrigin), options.Value.PipeName);
        LogOperatorActivity("Invitation link ready");
        return invitation;
    }
    /// <summary>Approves a pending guest connection.</summary>
    public void Approve(string connection) { engine.Approve(connection); LogOperatorActivity("Guest approved"); }

    /// <summary>Applies compatible motion limits without rearming control.</summary>
    public void ApplySessionLimits(int cap, int depth) { engine.SetLimits(new(cap, depth)); LogOperatorActivity("Session limits applied"); }

    /// <summary>Recovers the controller into Paused after device checks.</summary>
    public void Recover() { engine.Recover(); LogOperatorActivity("Connection ready; control paused"); }

    /// <summary>Arms an approved session after controller safety checks.</summary>
    public void Arm() { engine.Arm(); LogOperatorActivity("Control enabled"); }

    /// <summary>Stops motion and pauses control.</summary>
    public void Stop() { engine.Pause(); LogOperatorActivity("Stop requested"); }

    /// <summary>Ends the session and atomically replaces the invitation.</summary>
    public void End()
    {
        engine.CycleInvitation(new Uri(options.Value.GuestOrigin));
        LogOperatorActivity("Stop requested; session ended and invitation rotated");
    }
    /// <summary>Clears a matching external lock without rearming control.</summary>
    public void Unlock(long revision) { engine.Unlock(revision); LogOperatorActivity("Unlocked; control remains paused"); }

    private void LogOperatorActivity(string action) => logger.LogInformation(new EventId(200, "OperatorActivity"), "{Action}.", action);
    /// <summary>Injects an approved simulator fault for operator testing.</summary>
    public Task InjectSimulatorFaultAsync(string kind)
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
