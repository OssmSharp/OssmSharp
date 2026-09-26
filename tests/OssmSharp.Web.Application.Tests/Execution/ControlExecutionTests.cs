using OssmSharp.Web.Application.Models;

namespace OssmSharp.Web.Application.Tests.Execution;

/// <summary>
/// Checks execution deadlines and safety decisions without an application session or grant.
/// </summary>
public sealed class ControlExecutionTests
{
    [Fact]
    public async Task CoreArmsWithoutApplicationSessionOrGrant()
    {
        await using var rig = new CoreRig();

        rig.Engine.Recover();
        rig.Engine.Arm();

        Assert.Equal("Ready", rig.Engine.Snapshot.Mode);
        Assert.Equal(0, rig.Engine.Snapshot.Desired.Speed);
    }

    [Fact]
    public async Task AcceptedExecutionExpiresAtOriginalApplicationDeadline()
    {
        await using var rig = new CoreRig();
        rig.Engine.Recover();
        rig.Engine.Arm();
        rig.Engine.Submit(TestMotion.Settings(speed: 10), rig.Deadline(TimeSpan.FromMilliseconds(750)));
        rig.Pump();

        rig.Time.Advance(TimeSpan.FromMilliseconds(750));
        rig.Engine.Tick();

        Assert.Equal(("Paused", "grant-expired"),
            (rig.Engine.Snapshot.Mode, rig.Engine.Snapshot.Reason));
        Assert.Equal(0, rig.Engine.Snapshot.Desired.Speed);
    }

    [Fact]
    public async Task ConflictingLimitsRejectWithoutReplacingAcceptedExecution()
    {
        await using var rig = new CoreRig();
        rig.Engine.Recover();
        rig.Engine.Arm();
        var desired = TestMotion.Settings(speed: 40);
        rig.Engine.Submit(desired, rig.Deadline(TimeSpan.FromMilliseconds(750)));
        var before = rig.Engine.Snapshot;

        var error = Assert.Throws<ControlRejectedException>(() =>
            rig.Engine.SetLimits(new(20, 100)));

        Assert.Equal("limits-conflict", error.Code);
        Assert.Equal(before, rig.Engine.Snapshot);
    }

}
