using Microsoft.Extensions.Logging.Abstractions;

namespace OssmSharp.Device.Runtime.Tests;

public sealed class DeviceExecutionSessionTests
{
    [Fact]
    public async Task StaleAuthorityRevisionCannotInitializeOrIssueMotionAfterStop()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        await using var device = new TestDevice(clock, NullLogger<TestDevice>.Instance);
        await using var owner = new DeviceExecutionSession(device, clock);
        device.Connect();
        device.ReportState(Stopped(), clock.GetUtcNow().UtcDateTime);
        var initial = owner.AuthorityRevision;
        await owner.InitializeAsync(initial, TestContext.Current.CancellationToken);
        await owner.StopAsync(TestContext.Current.CancellationToken);

        Assert.NotEqual(initial, owner.AuthorityRevision);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            owner.InitializeAsync(initial, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            owner.SetAsync(
                Stopped() with { Speed = 10 }, initial,
                static settings => settings.Speed <= 100 && settings.Stroke <= settings.Depth,
                TestContext.Current.CancellationToken));
        Assert.Equal(2, device.DispatchCount); // Initialization and Stop only.
    }

    [Fact]
    public async Task InvalidFinalTransitionRejectsBeforePendingMutationOrAnyNewWrite()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        await using var device = new TestDevice(clock, NullLogger<TestDevice>.Instance);
        await using var owner = new DeviceExecutionSession(device, clock);
        var baseline = Stopped() with { Stroke = 30, Depth = 50 };
        device.Connect();
        device.ReportState(baseline, clock.GetUtcNow().UtcDateTime);
        await owner.InitializeAsync(owner.AuthorityRevision, TestContext.Current.CancellationToken);
        IReadOnlyList<MotionSettings>? planned = null;
        device.TransitionValidation = (_, steps) =>
        {
            planned = steps;
            if (steps.Any(step => step.Depth == 30))
                throw new InvalidOperationException("The physical envelope is not qualified.");
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            owner.SetAsync(
                baseline with { Speed = 10, Stroke = 20, Depth = 30 },
                owner.AuthorityRevision,
                static settings => settings.Stroke <= settings.Depth,
                TestContext.Current.CancellationToken));

        var steps = Assert.IsAssignableFrom<IReadOnlyList<MotionSettings>>(planned);
        Assert.Equal([0, 0, 0, 10], steps.Select(step => step.Speed));
        Assert.Equal([30, 20, 20, 20], steps.Select(step => step.Stroke));
        Assert.Equal([50, 50, 30, 30], steps.Select(step => step.Depth));
        Assert.Equal(1, device.DispatchCount); // Initialization only.
        Assert.Equal(DeviceExecutionState.Ready, owner.State);
    }

    [Fact]
    public async Task RejectedTransitionDoesNotReplaceAnAlreadyWaitingValidRequest()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        await using var device = new TestDevice(clock, NullLogger<TestDevice>.Instance);
        await using var owner = new DeviceExecutionSession(device, clock);
        device.Connect();
        device.ReportState(Stopped(), clock.GetUtcNow().UtcDateTime);
        await owner.InitializeAsync(owner.AuthorityRevision, TestContext.Current.CancellationToken);
        device.BlockDispatch = true;
        device.ReportStateDuringDispatch = true;
        device.TransitionValidation = (_, steps) =>
        {
            if (steps[^1].Speed == 20)
                throw new InvalidOperationException("Unqualified motion request.");
        };
        var issued = owner.SetAsync(
            Stopped() with { Speed = 10 }, owner.AuthorityRevision,
            static settings => settings.Stroke <= settings.Depth,
            TestContext.Current.CancellationToken);
        var pending = owner.SetAsync(
            Stopped() with { Speed = 12 }, owner.AuthorityRevision,
            static settings => settings.Stroke <= settings.Depth,
            TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            owner.SetAsync(
                Stopped() with { Speed = 20 }, owner.AuthorityRevision,
                static settings => settings.Stroke <= settings.Depth,
                TestContext.Current.CancellationToken));
        device.CompleteDispatch();
        await issued;
        await pending;

        Assert.Equal(3, device.DispatchCount); // Initialization and both accepted requests.
    }

    [Fact]
    public async Task PendingRequestRevalidatesWholeTransitionAgainstDequeueBaseline()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        await using var device = new TestDevice(clock, NullLogger<TestDevice>.Instance);
        await using var owner = new DeviceExecutionSession(device, clock);
        device.Connect();
        device.ReportState(Stopped(), clock.GetUtcNow().UtcDateTime);
        await owner.InitializeAsync(owner.AuthorityRevision, TestContext.Current.CancellationToken);
        var validatedBaselines = new List<int>();
        device.TransitionValidation = (reported, planned) =>
        {
            if (planned.Count <= 1 || planned[^1].Speed != 12)
                return;
            validatedBaselines.Add(reported.Speed);
            if (reported.Speed != 0)
                throw new InvalidOperationException("The reported baseline changed before issuance.");
        };
        device.BlockDispatch = true;
        var issued = owner.SetAsync(
            Stopped() with { Speed = 10 }, owner.AuthorityRevision,
            static settings => settings.Stroke <= settings.Depth,
            TestContext.Current.CancellationToken);
        var pending = owner.SetAsync(
            Stopped() with { Speed = 12, Stroke = 10 }, owner.AuthorityRevision,
            static settings => settings.Stroke <= settings.Depth,
            TestContext.Current.CancellationToken);
        device.ReportState(Stopped() with { Speed = 10 }, clock.GetUtcNow().UtcDateTime);
        device.ReportStateDuringDispatch = true;
        device.CompleteDispatch();

        await issued;
        await Assert.ThrowsAsync<InvalidOperationException>(() => pending);
        Assert.Equal([0, 10], validatedBaselines);
        Assert.Equal(2, device.DispatchCount); // Initialization and the first issued request only.
        Assert.Equal(DeviceExecutionState.Ready, owner.State);
    }

    [Fact]
    public async Task ConnectionAndStoppedFeedbackDoNotInitializeMotion()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        await using var device = new TestDevice(clock, NullLogger<TestDevice>.Instance);
        await using var owner = new DeviceExecutionSession(device, clock);
        device.Connect();
        device.ReportState(Stopped(), clock.GetUtcNow().UtcDateTime);

        Assert.Equal(DeviceExecutionState.Paused, owner.State);
        Assert.False(owner.IsMotionReady);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => owner.SetAsync(Stopped() with { Speed = 10 }, owner.AuthorityRevision, static settings => settings.Stroke <= settings.Depth && settings.Pattern == MotionPattern.SimpleStroke, TestContext.Current.CancellationToken));
        Assert.Equal(0, device.DispatchCount);
    }

    [Fact]
    public async Task StopWithoutReportedBaselineDoesNotInventMotionSettings()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        await using var device = new TestDevice(clock, NullLogger<TestDevice>.Instance);
        await using var owner = new DeviceExecutionSession(device, clock);
        device.Connect();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => owner.StopAsync(TestContext.Current.CancellationToken));
        Assert.Equal(DeviceExecutionState.Faulted, owner.State);
        Assert.Equal(0, device.DispatchCount);
    }

    [Fact]
    public async Task StopDuringPreparationWaitsForSettlementAndNeverPublishesReady()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        await using var device = new TestDevice(clock, NullLogger<TestDevice>.Instance)
        {
            BlockPreparation = true,
        };
        await using var owner = new DeviceExecutionSession(device, clock);
        device.Connect();
        device.ReportState(Stopped(), clock.GetUtcNow().UtcDateTime);

        var initialize = owner.InitializeAsync(owner.AuthorityRevision, TestContext.Current.CancellationToken);
        await device.PreparationStarted.WaitAsync(TestContext.Current.CancellationToken);
        var stop = owner.StopAsync(TestContext.Current.CancellationToken);
        Assert.False(stop.IsCompleted);
        Assert.Equal(1, device.DispatchCount); // Preflight zero already settled.
        device.CompletePreparation();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => initialize);
        await stop;
        Assert.Equal(DeviceExecutionState.Paused, owner.State);
        Assert.False(owner.IsMotionReady);
        Assert.Equal(2, device.DispatchCount); // Preflight zero and Stop; no late motion.
    }

    [Fact]
    public async Task TimedOutPreparationFaultsButRetainsWriterUntilSettled()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        await using var device = new TestDevice(clock, NullLogger<TestDevice>.Instance)
        {
            BlockPreparation = true,
        };
        await using var owner = new DeviceExecutionSession(device, clock);
        device.Connect();
        device.ReportState(Stopped(), clock.GetUtcNow().UtcDateTime);
        var initialization = owner.InitializeAsync(owner.AuthorityRevision, TestContext.Current.CancellationToken);
        await device.PreparationStarted.WaitAsync(TestContext.Current.CancellationToken);

        clock.Advance(TimeSpan.FromSeconds(6));
        await Assert.ThrowsAsync<TimeoutException>(() => initialization);
        Assert.Equal(DeviceExecutionState.Faulted, owner.State);
        var stop = owner.StopAsync(TestContext.Current.CancellationToken);
        Assert.False(stop.IsCompleted);
        Assert.Equal(1, device.DispatchCount); // Preflight zero before blocked configuration.

        device.CompletePreparation();
        await stop;
        Assert.Equal(2, device.DispatchCount); // Preflight zero, then Stop after configuration settled.
    }

    [Fact]
    public async Task RepeatedStopWhileUnsettledCoalescesWithoutAWriteBacklog()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        await using var device = new TestDevice(clock, NullLogger<TestDevice>.Instance);
        await using var owner = new DeviceExecutionSession(device, clock);
        device.Connect();
        device.ReportState(Stopped(), clock.GetUtcNow().UtcDateTime);
        await owner.InitializeAsync(owner.AuthorityRevision, TestContext.Current.CancellationToken);
        device.BlockDispatch = true;

        var first = owner.StopAsync(TestContext.Current.CancellationToken);
        var second = owner.StopAsync(TestContext.Current.CancellationToken);
        var third = owner.StopAsync(TestContext.Current.CancellationToken);
        Assert.False(first.IsCompleted);
        Assert.Equal(2, device.DispatchCount); // Initialization and one in-flight Stop.
        device.CompleteDispatch();
        await Task.WhenAll(first, second, third);

        Assert.Equal(2, device.DispatchCount);
    }

    [Fact]
    public async Task StaleIdleFeedbackInhibitsNewMotionWithoutFaultingOrInventingAHeartbeat()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        await using var device = new TestDevice(clock, NullLogger<TestDevice>.Instance);
        await using var owner = new DeviceExecutionSession(device, clock);
        device.Connect();
        device.ReportState(Stopped(), clock.GetUtcNow().UtcDateTime);
        await owner.InitializeAsync(owner.AuthorityRevision, TestContext.Current.CancellationToken);

        clock.Advance(TimeSpan.FromSeconds(6));

        Assert.Equal(DeviceExecutionState.Ready, owner.State);
        Assert.Null(owner.Fault);
        Assert.False(owner.IsMotionReady);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => owner.SetAsync(Stopped() with { Speed = 10 }, owner.AuthorityRevision, static settings => settings.Stroke <= settings.Depth && settings.Pattern == MotionPattern.SimpleStroke, TestContext.Current.CancellationToken));
        Assert.Equal(1, device.DispatchCount); // Initialization only; stale idle is not a transport failure.
    }

    [Fact]
    public async Task WaitingTupleRechecksItsOwnGuardBeforeIssuance()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        await using var device = new TestDevice(clock, NullLogger<TestDevice>.Instance);
        await using var owner = new DeviceExecutionSession(device, clock);
        device.Connect();
        device.ReportState(Stopped(), clock.GetUtcNow().UtcDateTime);
        await owner.InitializeAsync(owner.AuthorityRevision, TestContext.Current.CancellationToken);
        device.BlockDispatch = true;
        var issued = owner.SetAsync(
            Stopped() with { Speed = 10 }, owner.AuthorityRevision,
            static _ => true,
            TestContext.Current.CancellationToken);
        var guardPermitsLatest = true;
        var pending = owner.SetAsync(
            Stopped() with { Speed = 12 }, owner.AuthorityRevision,
            _ => guardPermitsLatest,
            TestContext.Current.CancellationToken);
        guardPermitsLatest = false;
        device.CompleteDispatch();
        device.ReportState(Stopped() with { Speed = 10 }, clock.GetUtcNow().UtcDateTime);

        await issued;
        await Assert.ThrowsAsync<OperationCanceledException>(() => pending);
        Assert.Equal(2, device.DispatchCount); // Initialization and first write only.
    }

    [Fact]
    public async Task ThrowingGuardFaultsAndRequestsOneSerializedZero()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        await using var device = new TestDevice(clock, NullLogger<TestDevice>.Instance);
        await using var owner = new DeviceExecutionSession(device, clock);
        device.Connect();
        device.ReportState(Stopped(), clock.GetUtcNow().UtcDateTime);
        await owner.InitializeAsync(owner.AuthorityRevision, TestContext.Current.CancellationToken);
        var guardError = new InvalidOperationException("Trusted motion guard failed.");

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => owner.SetAsync(
                Stopped() with { Speed = 10 }, owner.AuthorityRevision,
                _ => throw guardError,
                TestContext.Current.CancellationToken));

        Assert.Same(guardError, thrown);
        Assert.Same(guardError, owner.Fault);
        Assert.Equal(DeviceExecutionState.Faulted, owner.State);
        Assert.Equal(2, device.DispatchCount);
    }

    [Fact]
    public async Task DeniedInProcessGuardRejectsWithoutChangingReadyOwnerOrIssuingIO()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        await using var device = new TestDevice(clock, NullLogger<TestDevice>.Instance);
        await using var owner = new DeviceExecutionSession(device, clock);
        device.Connect();
        device.ReportState(Stopped(), clock.GetUtcNow().UtcDateTime);
        await owner.InitializeAsync(owner.AuthorityRevision, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => owner.SetAsync(
                Stopped() with { Speed = 10 }, owner.AuthorityRevision,
                static _ => false,
                TestContext.Current.CancellationToken));

        Assert.Equal(DeviceExecutionState.Ready, owner.State);
        Assert.Equal(1, device.DispatchCount); // Initialization zero only.
    }

    [Fact]
    public async Task RevokedAppliedGuardFaultsAndRequestsZeroWithoutNewInput()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        await using var device = new TestDevice(clock, NullLogger<TestDevice>.Instance);
        await using var owner = new DeviceExecutionSession(device, clock);
        device.Connect();
        device.ReportState(Stopped(), clock.GetUtcNow().UtcDateTime);
        await owner.InitializeAsync(owner.AuthorityRevision, TestContext.Current.CancellationToken);
        var authorized = true;
        var moving = Stopped() with { Speed = 10 };
        var issued = owner.SetAsync(
            moving, owner.AuthorityRevision, _ => authorized, TestContext.Current.CancellationToken);
        device.ReportState(moving, clock.GetUtcNow().UtcDateTime);
        await issued;

        authorized = false;
        clock.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(DeviceExecutionState.Faulted, owner.State);
        Assert.Equal(3, device.DispatchCount); // Initialization, motion, safety Stop.
        Assert.False(owner.IsMotionReady);
    }

    [Fact]
    public async Task ChangedNonzeroSpeedCannotUseThePreviousReportedBaselineAsFeedback()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        await using var device = new TestDevice(clock, NullLogger<TestDevice>.Instance);
        await using var owner = new DeviceExecutionSession(device, clock);
        device.Connect();
        device.ReportState(Stopped(), clock.GetUtcNow().UtcDateTime);
        await owner.InitializeAsync(owner.AuthorityRevision, TestContext.Current.CancellationToken);
        var previous = Stopped() with { Speed = 30 };
        var first = owner.SetAsync(previous, owner.AuthorityRevision, static settings => settings.Stroke <= settings.Depth && settings.Pattern == MotionPattern.SimpleStroke, TestContext.Current.CancellationToken);
        device.ReportState(previous, clock.GetUtcNow().UtcDateTime);
        await first;

        var changed = owner.SetAsync(
            Stopped() with { Speed = 80 }, owner.AuthorityRevision, static settings => settings.Stroke <= settings.Depth && settings.Pattern == MotionPattern.SimpleStroke, TestContext.Current.CancellationToken);
        Assert.False(changed.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(3));

        await Assert.ThrowsAsync<TimeoutException>(() => changed);
        Assert.Equal(DeviceExecutionState.Faulted, owner.State);
        Assert.Equal(4, device.DispatchCount); // Initialization, two writes, safety Stop.
    }

    [Fact]
    public async Task TriggeringKnobZeroCanAcknowledgeWithoutDuplicateNotificationWhenFresh()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        await using var device = new TestDevice(clock, NullLogger<TestDevice>.Instance);
        await using var owner = new DeviceExecutionSession(device, clock);
        device.Connect();
        device.ReportState(Stopped(), clock.GetUtcNow().UtcDateTime);
        await owner.InitializeAsync(owner.AuthorityRevision, TestContext.Current.CancellationToken);
        var moving = Stopped() with { Speed = 10 };
        var issued = owner.SetAsync(moving, owner.AuthorityRevision, static settings => settings.Stroke <= settings.Depth && settings.Pattern == MotionPattern.SimpleStroke, TestContext.Current.CancellationToken);
        device.ReportState(moving, clock.GetUtcNow().UtcDateTime);
        await issued;
        device.ReportState(Stopped(), clock.GetUtcNow().UtcDateTime);

        var revision = owner.InterruptionRevision;
        Assert.True(owner.IsInterrupted);
        Assert.True(await owner.AcknowledgeAsync(revision, owner.AuthorityRevision, TestContext.Current.CancellationToken));
        Assert.Equal(DeviceExecutionState.Paused, owner.State);
        Assert.Equal(3, device.DispatchCount); // Initialization, motion, and one lock Stop.
    }

    [Fact]
    public async Task StopInvalidatesCapturedAuthorityForPendingLockAcknowledgement()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        await using var device = new TestDevice(clock, NullLogger<TestDevice>.Instance);
        await using var owner = new DeviceExecutionSession(device, clock);
        device.Connect();
        device.ReportState(Stopped(), clock.GetUtcNow().UtcDateTime);
        await owner.InitializeAsync(owner.AuthorityRevision, TestContext.Current.CancellationToken);
        device.ReportState(Stopped() with { Speed = 5 }, clock.GetUtcNow().UtcDateTime);
        device.ReportState(Stopped(), clock.GetUtcNow().UtcDateTime);
        var lockRevision = owner.InterruptionRevision;
        var staleAuthority = owner.AuthorityRevision;

        await owner.StopAsync(TestContext.Current.CancellationToken);

        Assert.False(await owner.AcknowledgeAsync(
            lockRevision, staleAuthority, TestContext.Current.CancellationToken));
        Assert.True(owner.IsInterrupted);
        Assert.True(await owner.AcknowledgeAsync(
            lockRevision, owner.AuthorityRevision, TestContext.Current.CancellationToken));
        Assert.Equal(DeviceExecutionState.Paused, owner.State);
        Assert.False(owner.IsMotionReady);
    }

    [Fact]
    public async Task StaleTriggeringKnobZeroCannotClearTheInterruption()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        await using var device = new TestDevice(clock, NullLogger<TestDevice>.Instance);
        await using var owner = new DeviceExecutionSession(device, clock);
        device.Connect();
        device.ReportState(Stopped(), clock.GetUtcNow().UtcDateTime);
        await owner.InitializeAsync(owner.AuthorityRevision, TestContext.Current.CancellationToken);
        var moving = Stopped() with { Speed = 10 };
        var issued = owner.SetAsync(moving, owner.AuthorityRevision, static settings => settings.Stroke <= settings.Depth && settings.Pattern == MotionPattern.SimpleStroke, TestContext.Current.CancellationToken);
        device.ReportState(moving, clock.GetUtcNow().UtcDateTime);
        await issued;
        device.ReportState(Stopped(), clock.GetUtcNow().UtcDateTime);
        var revision = owner.InterruptionRevision;
        clock.Advance(TimeSpan.FromSeconds(6));

        Assert.False(await owner.AcknowledgeAsync(revision, owner.AuthorityRevision, TestContext.Current.CancellationToken));
        Assert.True(owner.IsInterrupted);
        Assert.Equal(DeviceExecutionState.Locked, owner.State);
    }

    [Fact]
    public async Task FaultAfterInterruptionCannotBeClearedByAcknowledgement()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        await using var device = new TestDevice(clock, NullLogger<TestDevice>.Instance);
        await using var owner = new DeviceExecutionSession(device, clock);
        device.Connect();
        device.ReportState(Stopped(), clock.GetUtcNow().UtcDateTime);
        await owner.InitializeAsync(owner.AuthorityRevision, TestContext.Current.CancellationToken);
        device.ReportState(Stopped() with { Speed = 5 }, clock.GetUtcNow().UtcDateTime);
        var revision = owner.InterruptionRevision;
        device.Fail(new InvalidDataException("Device feedback failed after the interruption."));
        device.ReportState(Stopped(), clock.GetUtcNow().UtcDateTime);

        Assert.Equal(DeviceExecutionState.Faulted, owner.State);
        Assert.False(await owner.AcknowledgeAsync(revision, owner.AuthorityRevision, TestContext.Current.CancellationToken));
        Assert.True(owner.IsInterrupted);
        Assert.False(owner.IsMotionReady);
    }

    [Fact]
    public async Task LeavingVerifiedPlayStateFaultsAndRequestsZeroWithoutRemoteNavigation()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        await using var device = new TestDevice(clock, NullLogger<TestDevice>.Instance);
        await using var owner = new DeviceExecutionSession(device, clock);
        device.Connect();
        device.ReportState(Stopped(), clock.GetUtcNow().UtcDateTime);
        await owner.InitializeAsync(owner.AuthorityRevision, TestContext.Current.CancellationToken);

        device.ReportState(Stopped() with { State = MotionDeviceState.MenuIdle }, clock.GetUtcNow().UtcDateTime);

        Assert.Equal(DeviceExecutionState.Faulted, owner.State);
        Assert.False(owner.IsMotionReady);
        Assert.Equal(2, device.DispatchCount); // Initialization and safety zero, no navigation.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => owner.SetAsync(Stopped() with { Speed = 10 }, owner.AuthorityRevision, static settings => settings.Stroke <= settings.Depth && settings.Pattern == MotionPattern.SimpleStroke, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task StoppedGeometryTransitionConfirmsShrinkThenDepthBeforeSpeed()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        await using var device = new TestDevice(clock, NullLogger<TestDevice>.Instance);
        await using var owner = new DeviceExecutionSession(device, clock);
        var baseline = Stopped() with { Stroke = 30, Depth = 50 };
        device.Connect();
        device.ReportState(baseline, clock.GetUtcNow().UtcDateTime);
        await owner.InitializeAsync(owner.AuthorityRevision, TestContext.Current.CancellationToken);
        device.ReportStateDuringDispatch = true;

        await owner.SetAsync(
            baseline with { Speed = 10, Stroke = 20, Depth = 30 },
            owner.AuthorityRevision,
            static settings => settings.Stroke <= settings.Depth,
            TestContext.Current.CancellationToken);

        await device.Dispatched.ReadAsync(TestContext.Current.CancellationToken); // Initialization zero.
        var zero = await device.Dispatched.ReadAsync(TestContext.Current.CancellationToken);
        var shrink = await device.Dispatched.ReadAsync(TestContext.Current.CancellationToken);
        var depth = await device.Dispatched.ReadAsync(TestContext.Current.CancellationToken);
        var speed = await device.Dispatched.ReadAsync(TestContext.Current.CancellationToken);
        Assert.Equal((0, 30, 50), (zero.Speed, zero.Stroke, zero.Depth));
        Assert.Equal((0, 20, 50), (shrink.Speed, shrink.Stroke, shrink.Depth));
        Assert.Equal((0, 20, 30), (depth.Speed, depth.Stroke, depth.Depth));
        Assert.Equal((10, 20, 30), (speed.Speed, speed.Stroke, speed.Depth));
    }

    [Fact]
    public async Task StopLatchedDuringFirstTransitionWriteBlocksEveryLaterGeometryWrite()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        await using var device = new TestDevice(clock, NullLogger<TestDevice>.Instance);
        await using var owner = new DeviceExecutionSession(device, clock);
        var baseline = Stopped() with { Stroke = 30, Depth = 50 };
        device.Connect();
        device.ReportState(baseline, clock.GetUtcNow().UtcDateTime);
        await owner.InitializeAsync(owner.AuthorityRevision, TestContext.Current.CancellationToken);
        device.BlockDispatch = true;

        var transition = owner.SetAsync(
            baseline with { Speed = 10, Stroke = 20, Depth = 30 },
            owner.AuthorityRevision,
            static settings => settings.Stroke <= settings.Depth,
            TestContext.Current.CancellationToken);
        Assert.Equal(2, device.DispatchCount); // Initialization plus the first zero.
        var stop = owner.StopAsync(TestContext.Current.CancellationToken);
        device.CompleteDispatch();

        await Assert.ThrowsAsync<OperationCanceledException>(() => transition);
        await stop;
        Assert.Equal(3, device.DispatchCount);
        await device.Dispatched.ReadAsync(TestContext.Current.CancellationToken);
        var first = await device.Dispatched.ReadAsync(TestContext.Current.CancellationToken);
        var final = await device.Dispatched.ReadAsync(TestContext.Current.CancellationToken);
        Assert.Equal((0, 30, 50), (first.Speed, first.Stroke, first.Depth));
        Assert.Equal((0, 30, 50), (final.Speed, final.Stroke, final.Depth));
    }

    [Fact]
    public async Task CallerCancellationAfterIssuanceSettlesThenStopsWithoutAnotherGeometryWrite()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        await using var device = new TestDevice(clock, NullLogger<TestDevice>.Instance);
        await using var owner = new DeviceExecutionSession(device, clock);
        var baseline = Stopped() with { Stroke = 30, Depth = 50 };
        device.Connect();
        device.ReportState(baseline, clock.GetUtcNow().UtcDateTime);
        await owner.InitializeAsync(owner.AuthorityRevision, TestContext.Current.CancellationToken);
        device.BlockDispatch = true;
        using var caller = new CancellationTokenSource();

        var transition = owner.SetAsync(
            baseline with { Speed = 10, Stroke = 20, Depth = 30 },
            owner.AuthorityRevision,
            static settings => settings.Stroke <= settings.Depth,
            caller.Token);
        Assert.Equal(2, device.DispatchCount);
        await caller.CancelAsync();
        Assert.True(device.RevocationObserved);
        Assert.Equal(2, device.DispatchCount);
        device.CompleteDispatch();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transition);
        Assert.Equal(3, device.DispatchCount);
        Assert.Equal(DeviceExecutionState.Paused, owner.State);
    }

    [Fact]
    public async Task DisposingReadyOwnerRequestsSerializedZeroFromReportedSettings()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        await using var device = new TestDevice(clock, NullLogger<TestDevice>.Instance);
        var owner = new DeviceExecutionSession(device, clock);
        device.Connect();
        device.ReportState(Stopped(), clock.GetUtcNow().UtcDateTime);
        await owner.InitializeAsync(owner.AuthorityRevision, TestContext.Current.CancellationToken);
        var moving = Stopped() with { Speed = 10 };
        var issued = owner.SetAsync(moving, owner.AuthorityRevision, static settings => settings.Stroke <= settings.Depth && settings.Pattern == MotionPattern.SimpleStroke, TestContext.Current.CancellationToken);
        device.ReportState(moving, clock.GetUtcNow().UtcDateTime);
        await issued;

        await owner.DisposeAsync();

        Assert.Equal(3, device.DispatchCount);
        await device.Dispatched.ReadAsync(TestContext.Current.CancellationToken);
        await device.Dispatched.ReadAsync(TestContext.Current.CancellationToken);
        var stopped = await device.Dispatched.ReadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, stopped.Speed);
        Assert.Equal(moving.Stroke, stopped.Stroke);
        Assert.Equal(DeviceExecutionState.Disposed, owner.State);
    }

    [Fact]
    public async Task StopStillIssuesZeroWhenAReportChangesGeometryDuringDispatch()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        await using var device = new TestDevice(clock, NullLogger<TestDevice>.Instance);
        await using var owner = new DeviceExecutionSession(device, clock);
        device.Connect();
        device.ReportState(Stopped(), clock.GetUtcNow().UtcDateTime);
        await owner.InitializeAsync(owner.AuthorityRevision, TestContext.Current.CancellationToken);
        var safetyStopDispatched = false;
        device.BeforeDispatch = (settings, safetyStop) =>
        {
            device.ReportState(Stopped() with { Stroke = 25, Depth = 50 }, clock.GetUtcNow().UtcDateTime);
            if (!safetyStop)
                throw new InvalidOperationException("Stale geometry is not an ordinary settings write.");
            safetyStopDispatched = true;
        };

        await owner.StopAsync(TestContext.Current.CancellationToken);

        Assert.True(safetyStopDispatched);
        Assert.Equal(2, device.DispatchCount); // Initialization and one serialized safety Stop.
        await device.Dispatched.ReadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, (await device.Dispatched.ReadAsync(TestContext.Current.CancellationToken)).Speed);
    }

    [Fact]
    public async Task StopKeepsMotionPausedUntilFreshExplicitInitialization()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        await using var device = new TestDevice(clock, NullLogger<TestDevice>.Instance);
        await using var owner = new DeviceExecutionSession(device, clock);
        device.Connect();
        device.ReportState(Stopped(), clock.GetUtcNow().UtcDateTime);
        await owner.InitializeAsync(owner.AuthorityRevision, TestContext.Current.CancellationToken);

        await owner.StopAsync(TestContext.Current.CancellationToken);
        Assert.Equal(DeviceExecutionState.Paused, owner.State);
        Assert.False(owner.IsMotionReady);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => owner.SetAsync(Stopped() with { Speed = 10 }, owner.AuthorityRevision, static settings => settings.Stroke <= settings.Depth && settings.Pattern == MotionPattern.SimpleStroke, TestContext.Current.CancellationToken));
        await owner.SetAsync(Stopped(), owner.AuthorityRevision, static settings => settings.Stroke <= settings.Depth && settings.Pattern == MotionPattern.SimpleStroke, TestContext.Current.CancellationToken);
        Assert.Equal(DeviceExecutionState.Paused, owner.State);

        await owner.InitializeAsync(owner.AuthorityRevision, TestContext.Current.CancellationToken);
        Assert.True(owner.IsMotionReady);
    }

    [Fact]
    public async Task StopInvalidatesWaitingMotionAndNeverStartsAnotherWriter()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        await using var device = new TestDevice(clock, NullLogger<TestDevice>.Instance);
        await using var owner = new DeviceExecutionSession(device, clock);
        device.Connect();
        device.ReportState(Stopped(), clock.GetUtcNow().UtcDateTime);
        await owner.InitializeAsync(owner.AuthorityRevision, TestContext.Current.CancellationToken);
        device.BlockDispatch = true;
        var first = owner.SetAsync(Stopped() with { Speed = 10 }, owner.AuthorityRevision, static settings => settings.Stroke <= settings.Depth && settings.Pattern == MotionPattern.SimpleStroke, TestContext.Current.CancellationToken);
        await device.DispatchStarted.WaitAsync(TestContext.Current.CancellationToken);
        var pending = owner.SetAsync(Stopped() with { Speed = 11 }, owner.AuthorityRevision, static settings => settings.Stroke <= settings.Depth && settings.Pattern == MotionPattern.SimpleStroke, TestContext.Current.CancellationToken);

        var stop = owner.StopAsync(TestContext.Current.CancellationToken);
        Assert.False(stop.IsCompleted);
        Assert.True(device.RevocationObserved);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => owner.SetAsync(Stopped() with { Speed = 12 }, owner.AuthorityRevision, static settings => settings.Stroke <= settings.Depth && settings.Pattern == MotionPattern.SimpleStroke, TestContext.Current.CancellationToken));
        device.CompleteDispatch();
        await Assert.ThrowsAsync<OperationCanceledException>(() => pending);
        await Assert.ThrowsAsync<OperationCanceledException>(() => first);
        await stop;
        Assert.Equal(3, device.DispatchCount);
    }

    [Fact]
    public async Task NewPendingTupleSupersedesOnlyTheOlderWaitingTuple()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        await using var device = new TestDevice(clock, NullLogger<TestDevice>.Instance);
        await using var owner = new DeviceExecutionSession(device, clock);
        device.Connect();
        device.ReportState(Stopped(), clock.GetUtcNow().UtcDateTime);
        await owner.InitializeAsync(owner.AuthorityRevision, TestContext.Current.CancellationToken);
        device.BlockDispatch = true;
        var issued = owner.SetAsync(Stopped() with { Speed = 10 }, owner.AuthorityRevision, static settings => settings.Stroke <= settings.Depth && settings.Pattern == MotionPattern.SimpleStroke, TestContext.Current.CancellationToken);
        Assert.Equal(2, device.DispatchCount);
        var older = owner.SetAsync(Stopped() with { Speed = 11 }, owner.AuthorityRevision, static settings => settings.Stroke <= settings.Depth && settings.Pattern == MotionPattern.SimpleStroke, TestContext.Current.CancellationToken);
        var latest = owner.SetAsync(Stopped() with { Speed = 12 }, owner.AuthorityRevision, static settings => settings.Stroke <= settings.Depth && settings.Pattern == MotionPattern.SimpleStroke, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<OperationCanceledException>(() => older);
        device.CompleteDispatch();
        device.ReportState(Stopped() with { Speed = 10 }, clock.GetUtcNow().UtcDateTime);
        await issued;
        await device.Dispatched.ReadAsync(TestContext.Current.CancellationToken);
        await device.Dispatched.ReadAsync(TestContext.Current.CancellationToken);
        await device.Dispatched.ReadAsync(TestContext.Current.CancellationToken);
        device.ReportState(Stopped() with { Speed = 12 }, clock.GetUtcNow().UtcDateTime);
        await latest;
        Assert.Equal(3, device.DispatchCount);
    }

    [Fact]
    public async Task IssuedMotionWaitsForActualReportedFeedbackAfterWriteSettlement()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        await using var device = new TestDevice(clock, NullLogger<TestDevice>.Instance);
        await using var owner = new DeviceExecutionSession(device, clock);
        device.Connect();
        device.ReportState(Stopped(), clock.GetUtcNow().UtcDateTime);
        await owner.InitializeAsync(owner.AuthorityRevision, TestContext.Current.CancellationToken);
        var requested = Stopped() with { Speed = 10 };

        var write = owner.SetAsync(requested, owner.AuthorityRevision, static settings => settings.Stroke <= settings.Depth && settings.Pattern == MotionPattern.SimpleStroke, TestContext.Current.CancellationToken);
        Assert.Equal(2, device.DispatchCount);
        Assert.False(write.IsCompleted);
        device.ReportState(requested, clock.GetUtcNow().UtcDateTime);

        await write;
        Assert.Equal(DeviceExecutionState.Ready, owner.State);
    }

    [Fact]
    public async Task InterruptionRevisionRequiresMatchingAcknowledgementAndLeavesPaused()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        await using var device = new TestDevice(clock, NullLogger<TestDevice>.Instance);
        await using var owner = new DeviceExecutionSession(device, clock);
        device.Connect();
        device.ReportState(Stopped(), clock.GetUtcNow().UtcDateTime);
        await owner.InitializeAsync(owner.AuthorityRevision, TestContext.Current.CancellationToken);
        device.ReportState(Stopped() with { Speed = 5 }, clock.GetUtcNow().UtcDateTime);
        var revision = owner.InterruptionRevision;
        Assert.True(revision > 0);

        Assert.False(await owner.AcknowledgeAsync(revision - 1, owner.AuthorityRevision, TestContext.Current.CancellationToken));
        Assert.False(await owner.AcknowledgeAsync(revision, owner.AuthorityRevision, TestContext.Current.CancellationToken));
        Assert.True(owner.IsInterrupted);
        device.ReportState(Stopped(), clock.GetUtcNow().UtcDateTime);
        Assert.True(await owner.AcknowledgeAsync(revision, owner.AuthorityRevision, TestContext.Current.CancellationToken));
        Assert.Equal(DeviceExecutionState.Paused, owner.State);
        Assert.False(owner.IsInterrupted);
    }

    [Fact]
    public async Task StaleFeedbackRejectsNewMotionButStillAllowsStop()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        await using var device = new TestDevice(clock, NullLogger<TestDevice>.Instance);
        await using var owner = new DeviceExecutionSession(device, clock);
        device.Connect();
        device.ReportState(Stopped(), clock.GetUtcNow().UtcDateTime);
        await owner.InitializeAsync(owner.AuthorityRevision, TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromSeconds(6));

        Assert.Equal(DeviceExecutionState.Ready, owner.State);
        Assert.Null(owner.Fault);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => owner.SetAsync(Stopped() with { Speed = 10 }, owner.AuthorityRevision, static settings => settings.Stroke <= settings.Depth && settings.Pattern == MotionPattern.SimpleStroke, TestContext.Current.CancellationToken));
        await owner.StopAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, device.DispatchCount); // Initialization and explicit Stop only.
    }

    [Fact]
    public async Task CancelledStopWaitRetainsIssuedOperationAndFaultsUnknownOutcome()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        await using var device = new TestDevice(clock, NullLogger<TestDevice>.Instance);
        await using var owner = new DeviceExecutionSession(device, clock);
        device.Connect();
        device.ReportState(Stopped(), clock.GetUtcNow().UtcDateTime);
        await owner.InitializeAsync(owner.AuthorityRevision, TestContext.Current.CancellationToken);
        device.BlockDispatch = true;
        var issued = owner.SetAsync(Stopped() with { Speed = 10 }, owner.AuthorityRevision, static settings => settings.Stroke <= settings.Depth && settings.Pattern == MotionPattern.SimpleStroke, TestContext.Current.CancellationToken);
        Assert.Equal(2, device.DispatchCount);
        using var cancelledWait = new CancellationTokenSource();
        var stop = owner.StopAsync(cancelledWait.Token);
        await cancelledWait.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stop);
        Assert.Equal(DeviceExecutionState.Faulted, owner.State);
        Assert.Equal(2, device.DispatchCount);
        device.CompleteDispatch();
        await Assert.ThrowsAsync<OperationCanceledException>(() => issued);
        await owner.DisposeAsync();
        Assert.Equal(3, device.DispatchCount);
        Assert.Equal(DeviceExecutionState.Disposed, owner.State);
    }

    [Fact]
    public async Task TimedOutMotionKeepsIssuedWriterUntilNativeSettlement()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        await using var device = new TestDevice(clock, NullLogger<TestDevice>.Instance);
        await using var owner = new DeviceExecutionSession(device, clock);
        device.Connect();
        device.ReportState(Stopped(), clock.GetUtcNow().UtcDateTime);
        await owner.InitializeAsync(owner.AuthorityRevision, TestContext.Current.CancellationToken);
        device.BlockDispatch = true;
        var issued = owner.SetAsync(Stopped() with { Speed = 10 }, owner.AuthorityRevision, static settings => settings.Stroke <= settings.Depth && settings.Pattern == MotionPattern.SimpleStroke, TestContext.Current.CancellationToken);
        Assert.Equal(2, device.DispatchCount);

        clock.Advance(TimeSpan.FromSeconds(6));
        await Assert.ThrowsAsync<TimeoutException>(() => issued);
        Assert.Equal(DeviceExecutionState.Faulted, owner.State);
        var stop = owner.StopAsync(TestContext.Current.CancellationToken);
        Assert.False(stop.IsCompleted);
        Assert.Equal(2, device.DispatchCount);

        device.CompleteDispatch();
        await stop;
        Assert.Equal(3, device.DispatchCount);
    }

    [Fact]
    public async Task DisposalDeadlineUsesInjectedClockWithoutReleasingIssuedStop()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        await using var device = new TestDevice(clock, NullLogger<TestDevice>.Instance);
        await using var owner = new DeviceExecutionSession(device, clock);
        device.Connect();
        device.ReportState(Stopped(), clock.GetUtcNow().UtcDateTime);
        await owner.InitializeAsync(owner.AuthorityRevision, TestContext.Current.CancellationToken);
        device.BlockDispatch = true;
        var stop = owner.StopAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, device.DispatchCount); // Initialization and issued zero.

        try
        {
            var disposal = owner.DisposeAsync().AsTask();
            Assert.False(disposal.IsCompleted);
            clock.Advance(TimeSpan.FromSeconds(6));
            await Assert.ThrowsAsync<TimeoutException>(
                () => disposal.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
            Assert.True(disposal.IsCompleted);
            Assert.False(stop.IsCompleted);
            Assert.Equal(2, device.DispatchCount);
        }
        finally
        {
            device.CompleteDispatch();
            await stop.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task ActiveMotionWithoutFreshFeedbackFaultsAndRequestsZeroOnExpiry()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        await using var device = new TestDevice(clock, NullLogger<TestDevice>.Instance);
        await using var owner = new DeviceExecutionSession(device, clock);
        device.Connect();
        device.ReportState(Stopped(), clock.GetUtcNow().UtcDateTime);
        await owner.InitializeAsync(owner.AuthorityRevision, TestContext.Current.CancellationToken);
        var requested = Stopped() with { Speed = 10 };
        var moving = owner.SetAsync(requested, owner.AuthorityRevision, static settings => settings.Stroke <= settings.Depth && settings.Pattern == MotionPattern.SimpleStroke, TestContext.Current.CancellationToken);
        device.ReportState(requested, clock.GetUtcNow().UtcDateTime);
        await moving;
        Assert.Equal(2, device.DispatchCount);

        clock.Advance(TimeSpan.FromSeconds(6));

        Assert.Equal(DeviceExecutionState.Faulted, owner.State);
        Assert.Equal(3, device.DispatchCount);
        Assert.IsType<TimeoutException>(owner.Fault);
    }

    private static MotionSettings Stopped() =>
        new(MotionDeviceState.StrokeEngineIdle, 0, 20, 50, 40, 0, MotionPattern.SimpleStroke, 0);
}
