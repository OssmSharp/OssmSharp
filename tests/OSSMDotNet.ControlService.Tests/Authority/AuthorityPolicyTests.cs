using OSSMDotNet.ControlCore.Tests;
using OSSMDotNet.Models;

namespace OSSMDotNet.ControlService.Tests.Authority;

/// <summary>
/// Checks that policy changes preserve accepted motion while rejecting stale guest authority.
/// </summary>
public sealed class AuthorityPolicyTests
{
    [Fact]
    public async Task AuthenticatedRejectedInputsConsumeRateLimitTokens()
    {
        await using var rig = new AuthorityRig();
        rig.Arm();
        var stale = rig.Input(1);
        rig.Engine.SetLimits(new(25, 80));

        for (var sequence = 1; sequence <= 10; sequence++)
            Assert.Equal("policy-invalid", Assert.Throws<ControlRejectedException>(() =>
                rig.Submit(stale with { Seq = sequence })).Code);

        Assert.Equal("rate-limited", Assert.Throws<ControlRejectedException>(() =>
            rig.Submit(rig.Input(1))).Code);
    }

    [Theory]
    [InlineData(25, 80)]
    [InlineData(100, 100)]
    public async Task CompatiblePolicyChangePreservesAcceptedMotionAndPublishesFreshGrant(int speed, int depth)
    {
        await using var rig = new AuthorityRig();
        rig.Arm();
        var input = rig.Input(1);
        rig.Submit(input);
        rig.Pump();
        var before = rig.Engine.Snapshot;

        rig.Engine.SetLimits(new(speed, depth));

        var after = rig.Engine.Snapshot;
        Assert.Equal("Ready", after.Mode);
        Assert.Equal(before.Generation, after.Generation);
        Assert.Equal(before.AckSeq, after.AckSeq);
        Assert.Equal(before.VerifiedSeq, after.VerifiedSeq);
        Assert.Equal(before.Desired, after.Desired);
        Assert.Equal(new ControlLimits(speed, depth), after.Limits);
        Assert.Equal(before.PolicyVersion + 1, after.PolicyVersion);
        Assert.Equal(input.Epoch, after.Grant!.Epoch);
        Assert.NotEqual(input.GrantId, after.Grant.Id);
    }

    [Fact]
    public async Task ReapplyingSameLimitsIsANoopIncludingGrantAndSequence()
    {
        await using var rig = new AuthorityRig();
        rig.Arm();
        rig.Submit(rig.Input(1));
        rig.Pump();
        var before = rig.Engine.Snapshot;

        rig.Engine.SetLimits(new(50, 100));

        Assert.Equal(before, rig.Engine.Snapshot);
        Assert.Single(rig.Bridge.Calls);
    }

    [Fact]
    public async Task OldPolicyAndSupersededGrantRejectWithoutChangingAcceptedIntent()
    {
        await using var rig = new AuthorityRig();
        rig.Arm();
        var old = rig.Input(1);
        rig.Submit(old);
        rig.Engine.SetLimits(new(25, 80));
        var before = rig.Engine.Snapshot;

        Assert.Equal("policy-invalid", Assert.Throws<ControlRejectedException>(() =>
            rig.Submit(old with { Seq = 2 })).Code);
        Assert.Equal("grant-invalid", Assert.Throws<ControlRejectedException>(() =>
            rig.Submit(old with { Seq = 2, PolicyVersion = before.PolicyVersion })).Code);
        Assert.Equal(before, rig.Engine.Snapshot);
    }

    [Fact]
    public async Task ApplyAndStatusDoNotRenewTheOriginalAcceptedLease()
    {
        await using var rig = new AuthorityRig();
        rig.Arm();
        rig.Submit(rig.Input(1));
        rig.Pump();
        rig.Advance(500);
        rig.Engine.SetLimits(new(25, 80));
        var fresh = rig.Engine.Snapshot.Grant!;
        Assert.Equal(750, fresh.ValidForMs);
        rig.Advance(249);
        Assert.Equal("Ready", rig.Engine.GuestSnapshot(
            rig.Guest.SessionId, rig.Guest.GuestToken, rig.Guest.ConnectionId).Mode);

        rig.Advance(1);
        rig.Pump();

        Assert.Equal(("Paused", "grant-expired"),
            (rig.Engine.Snapshot.Mode, rig.Engine.Snapshot.Reason));
        Assert.Null(rig.Engine.Snapshot.Grant);
    }

    [Fact]
    public async Task ExplicitGuestInputCanRenewWithFreshPolicyGrantButCannotOutliveItsDeadline()
    {
        await using var rig = new AuthorityRig();
        rig.Arm();
        rig.Submit(rig.Input(1));
        rig.Pump();
        rig.Advance(500);
        rig.Engine.SetLimits(new(25, 80));
        rig.Submit(rig.Input(2));
        rig.Pump();

        rig.Advance(749);
        Assert.Equal("Ready", rig.Engine.Snapshot.Mode);
        rig.Advance(1);

        Assert.Equal(("Paused", "grant-expired"),
            (rig.Engine.Snapshot.Mode, rig.Engine.Snapshot.Reason));
    }

    [Fact]
    public async Task ApplyAfterMissedLeaseDeadlineStopsInsteadOfRevivingAuthority()
    {
        await using var rig = new AuthorityRig();
        rig.Arm();
        rig.Submit(rig.Input(1));
        rig.Pump();
        rig.Time.Advance(TimeSpan.FromMilliseconds(750));
        rig.Bridge.Publish();

        rig.Engine.SetLimits(new(25, 80));

        Assert.Equal(("Paused", "grant-expired"),
            (rig.Engine.Snapshot.Mode, rig.Engine.Snapshot.Reason));
        Assert.Null(rig.Engine.Snapshot.Grant);
        Assert.True(rig.Engine.Snapshot.OperationPending);
    }

    [Fact]
    public async Task GrantPublishesImmediatelyThenEvery250MillisecondsWithoutExtendingOldGrant()
    {
        await using var rig = new AuthorityRig();
        rig.Arm();
        var first = rig.Engine.Snapshot.Grant!;
        var input = rig.Input(1);
        rig.Advance(249);
        Assert.Equal(first.Id, rig.Engine.Snapshot.Grant!.Id);
        rig.Advance(1);
        Assert.NotEqual(first.Id, rig.Engine.Snapshot.Grant!.Id);
        rig.Advance(499);
        rig.Submit(input);
        Assert.Equal(1L, rig.Engine.Snapshot.AckSeq);
        rig.Advance(1);

        Assert.Equal("Paused", rig.Engine.Snapshot.Mode);
        Assert.Throws<ControlRejectedException>(() => rig.Submit(input with { Seq = 2 }));
    }

    [Fact]
    public async Task MotionRejectsVersionEpochPolicyGrantSequenceAndMissingRunWithoutRenewal()
    {
        await using var rig = new AuthorityRig();
        rig.Arm();
        var valid = rig.Input(1);
        foreach (var invalid in new[]
        {
            valid with { V = 1 }, valid with { Epoch = "old" }, valid with { PolicyVersion = -1 },
            valid with { GrantId = "unknown" }, valid with { Seq = 0 },
            valid with { Seq = 9007199254740992 }, valid with { Run = false }
        })
            Assert.Throws<ControlRejectedException>(() => rig.Submit(invalid));
        Assert.Equal(0L, rig.Engine.Snapshot.AckSeq);
        Assert.Empty(rig.Bridge.Calls);

        rig.Submit(valid);
        Assert.Equal("sequence-invalid", Assert.Throws<ControlRejectedException>(() => rig.Submit(valid)).Code);
    }

    [Fact]
    public async Task RollingAttemptWindowUsesMonotonicExactBoundaryAndIgnoresWallClockJumps()
    {
        await using var rig = new AuthorityRig();
        rig.Arm();
        var stale = rig.Input(1);
        rig.Engine.SetLimits(new(25, 80));
        for (var sequence = 1; sequence <= 10; sequence++)
            Assert.Equal("policy-invalid", Assert.Throws<ControlRejectedException>(() =>
                rig.Submit(stale with { Seq = sequence })).Code);

        Assert.Equal("rate-limited", Assert.Throws<ControlRejectedException>(() =>
            rig.Submit(stale with { Seq = 11 })).Code);
        rig.Time.Advance(TimeSpan.FromMilliseconds(999));
        rig.Time.Offset = TimeSpan.FromDays(1);
        Assert.Equal("rate-limited", Assert.Throws<ControlRejectedException>(() =>
            rig.Submit(stale with { Seq = 12 })).Code);
        rig.Time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal("policy-invalid", Assert.Throws<ControlRejectedException>(() =>
            rig.Submit(stale with { Seq = 13 })).Code);
    }

    [Fact]
    public async Task StopAndLeaveRemainAvailableAfterInputLimitIsReached()
    {
        await using var rig = new AuthorityRig();
        rig.Arm();
        var first = rig.Input(1);
        for (var sequence = 1; sequence <= 10; sequence++)
            rig.Submit(first with { Seq = sequence });
        Assert.Equal("rate-limited", Assert.Throws<ControlRejectedException>(() =>
            rig.Submit(first with { Seq = 11 })).Code);

        rig.Engine.GuestStop(rig.Guest.SessionId, rig.Guest.GuestToken, rig.Guest.ConnectionId);
        rig.Engine.Leave(rig.Guest.SessionId, rig.Guest.GuestToken, rig.Guest.ConnectionId);

        Assert.Equal("Ended", rig.Engine.Snapshot.SessionMode);
    }

    [Fact]
    public async Task RejectedExcessAttemptDoesNotReplacePendingWorkOrExtendGrantDeadline()
    {
        await using var rig = new AuthorityRig();
        rig.Arm();
        rig.Bridge.AutoComplete = false;
        var first = rig.Input(1);
        rig.Submit(first);
        rig.Engine.Tick();
        for (var sequence = 2; sequence <= 10; sequence++)
            rig.Submit(first with { Seq = sequence });
        var before = rig.Engine.Snapshot;

        Assert.Equal("rate-limited", Assert.Throws<ControlRejectedException>(() =>
            rig.Submit(first with { Seq = 11 })).Code);
        Assert.Equal(before, rig.Engine.Snapshot);
        Assert.True(before.OperationPending);
        Assert.Single(rig.Bridge.Calls);
        rig.Time.Advance(TimeSpan.FromMilliseconds(750));
        rig.Engine.Tick();
        Assert.Equal(("Paused", "grant-expired"),
            (rig.Engine.Snapshot.Mode, rig.Engine.Snapshot.Reason));
    }

    [Fact]
    public async Task PolicyChangePreservesReadyAndEpochButRejectsTheOldPolicy()
    {
        await using var rig = new AuthorityRig();
        rig.Arm();
        var old = rig.Input(1);
        var generation = rig.Engine.Snapshot.Generation;

        rig.Engine.SetLimits(new(25, 80));

        Assert.Equal("Ready", rig.Engine.Snapshot.Mode);
        Assert.Equal(generation, rig.Engine.Snapshot.Generation);
        Assert.Equal(old.Epoch, rig.Engine.Snapshot.Grant!.Epoch);
        Assert.Equal(old.PolicyVersion + 1, rig.Engine.Snapshot.PolicyVersion);
        Assert.Equal("policy-invalid", Assert.Throws<ControlRejectedException>(() => rig.Submit(old)).Code);
    }

    [Fact]
    public async Task TransportLossAndFixedSessionExpiryEndAuthority()
    {
        await using var rig = new AuthorityRig();
        rig.Arm();
        rig.Engine.TransportLost("unrelated");
        Assert.Equal("Ready", rig.Engine.Snapshot.Mode);
        rig.Engine.TransportLost(rig.Guest.ConnectionId);
        Assert.Equal("Paused", rig.Engine.Snapshot.Mode);
        Assert.Equal("Ended", rig.Engine.Snapshot.SessionMode);

        await using var expires = new AuthorityRig();
        expires.Arm();
        expires.Time.Advance(TimeSpan.FromMinutes(30));
        expires.Bridge.Publish();
        expires.Engine.Tick();
        Assert.Equal("Ended", expires.Engine.Snapshot.SessionMode);
        Assert.Equal("Paused", expires.Engine.Snapshot.Mode);
    }
}
