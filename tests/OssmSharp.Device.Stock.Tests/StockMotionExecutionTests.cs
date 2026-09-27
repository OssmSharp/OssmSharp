namespace OssmSharp.Device.Stock.Tests;

public sealed class StockMotionExecutionTests
{
    [Fact]
    public async Task SafetyStopWritesOnlyZeroAfterAChangeToReportedGeometry()
    {
        await using var scenario = await StockMotionScenario.ConnectAsync(
            new UnqualifiedStockMotion(), 25, 50, TestContext.Current.CancellationToken);
        await scenario.Execution.DisposeAsync();
        var previous = new MotionSettings(
            MotionDeviceState.StrokeEngineIdle, 0, 25, 50, 50, 0, MotionPattern.SimpleStroke, 0);
        var changedReport = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scenario.Device.DeviceStateChanged += change =>
        {
            if (change.IsT0 && change.AsT0.MotionSettings.Stroke == 50)
                changedReport.TrySetResult();
        };
        Assert.True(scenario.TryReport(0, 50, 75));
        await changedReport.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            scenario.Device.SetMotionAsync(previous, TestContext.Current.CancellationToken));
        var stop = (OssmSharp.Device.Runtime.IDeviceSafetyStop)scenario.Device;
        await Assert.ThrowsAsync<ArgumentException>(() =>
            stop.SetSafetyStopAsync(previous with { Speed = 12 }, TestContext.Current.CancellationToken));
        Assert.Empty(scenario.Connection.Commands);

        await stop.SetSafetyStopAsync(previous, TestContext.Current.CancellationToken);

        Assert.Equal(["set:speed:0"], scenario.Connection.Commands);
    }

    [Theory]
    [InlineData("menu.idle")]
    [InlineData("strokeEngine.pattern")]
    public async Task StopFromNonPlayableReportAlwaysWritesSpeedOnlyZero(string state)
    {
        await using var scenario = await StockMotionScenario.ConnectAsync(
            new UnqualifiedStockMotion(), 20, 40,
            TestContext.Current.CancellationToken, initialState: state);

        await scenario.Execution.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["set:speed:0"], scenario.Connection.Commands);
        Assert.Equal(OssmSharp.Device.Runtime.DeviceExecutionState.Paused, scenario.Execution.State);
    }

    [Fact]
    public async Task MismatchedResolvedQualificationNeverPublishesAConnectedDevice()
    {
        await using var scenario = StockMotionScenario.Create(new TestStockQualificationResolver(mismatched: true));
        var transitions = new List<DeviceConnectionState>();
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scenario.Device.ConnectionStateChanged += state =>
        {
            transitions.Add(state);
            if (state == DeviceConnectionState.Disconnected)
                disconnected.TrySetResult();
        };

        await scenario.Device.StartAsync(TestContext.Current.CancellationToken);
        await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        Assert.Equal([DeviceConnectionState.Connecting, DeviceConnectionState.Disconnected], transitions);
        Assert.Empty(scenario.Connection.Commands);
    }

    [Fact]
    public async Task NativePositiveOutputExpiresWithoutNewChangeDrivenFeedback()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var scenario = await StockMotionScenario.ConnectAsync(
            new TestStockQualificationResolver(), 25, 50,
            TestContext.Current.CancellationToken, clock);
        var execution = scenario.Execution;
        await execution.InitializeAsync(execution.AuthorityRevision, TestContext.Current.CancellationToken);
        scenario.Connection.AfterWrite = command =>
        {
            if (command.Setting == "speed" && command.Value != 0)
                scenario.TryReport(command.Value, 25, 50);
        };
        await execution.SetAsync(
            new MotionSettings(MotionDeviceState.StrokeEngineIdle, 12, 25, 50, 50, 0, MotionPattern.SimpleStroke, 0),
            execution.AuthorityRevision,
            static settings => settings.Speed <= 12 && settings.Stroke <= settings.Depth,
            TestContext.Current.CancellationToken);

        clock.Advance(TimeSpan.FromSeconds(6));

        Assert.Equal(OssmSharp.Device.Runtime.DeviceExecutionState.Faulted, execution.State);
        Assert.Equal(["set:speed:0", "set:speed:12", "set:speed:0"], scenario.Connection.Commands);
    }

    [Fact]
    public async Task UnqualifiedTransitionRejectsWholePlanBeforeItsFirstZeroWrite()
    {
        await using var scenario = await StockMotionScenario.ConnectAsync(
            new UnqualifiedStockMotion(), 25, 50, TestContext.Current.CancellationToken);
        var execution = scenario.Execution;
        await execution.InitializeAsync(execution.AuthorityRevision, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            execution.SetAsync(
                new MotionSettings(MotionDeviceState.StrokeEngineIdle, 12, 25, 50, 50, 0, MotionPattern.SimpleStroke, 0),
                execution.AuthorityRevision,
                static settings => settings.Speed <= 12 && settings.Stroke <= settings.Depth,
                TestContext.Current.CancellationToken));

        Assert.Equal(["set:speed:0"], scenario.Connection.Commands); // Initialization only.
        Assert.Equal(OssmSharp.Device.Runtime.DeviceExecutionState.Ready, execution.State);
    }

    [Fact]
    public async Task QualifiedMaximumSpeedRejectsHigherMotionBeforeAnyTransitionWrite()
    {
        await using var scenario = await StockMotionScenario.ConnectAsync(
            new TestStockQualificationResolver(maximumSpeedPercent: 12), 25, 50,
            TestContext.Current.CancellationToken);
        var execution = scenario.Execution;
        await execution.InitializeAsync(execution.AuthorityRevision, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            execution.SetAsync(
                new MotionSettings(MotionDeviceState.StrokeEngineIdle, 13, 25, 50, 50, 0, MotionPattern.SimpleStroke, 0),
                execution.AuthorityRevision,
                static settings => settings.Speed <= 13 && settings.Stroke <= settings.Depth,
                TestContext.Current.CancellationToken));

        Assert.Equal(["set:speed:0"], scenario.Connection.Commands); // Initialization only.
        Assert.Equal(OssmSharp.Device.Runtime.DeviceExecutionState.Ready, execution.State);
    }

    [Fact]
    public async Task UnsupportedStoppedBaselineRejectsTheEntirePlanWithoutWrites()
    {
        await using var scenario = await StockMotionScenario.ConnectAsync(
            new TestStockQualificationResolver(), 50, 75,
            TestContext.Current.CancellationToken, initialSensation: 70);
        var execution = scenario.Execution;
        await execution.InitializeAsync(execution.AuthorityRevision, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            execution.SetAsync(
                new MotionSettings(MotionDeviceState.StrokeEngineIdle, 12, 25, 50, 50, 0, MotionPattern.SimpleStroke, 0),
                execution.AuthorityRevision,
                static settings => settings.Speed <= 12 && settings.Stroke <= settings.Depth,
                TestContext.Current.CancellationToken));

        Assert.Equal(["set:speed:0"], scenario.Connection.Commands); // Initialization only.
        Assert.Equal(OssmSharp.Device.Runtime.DeviceExecutionState.Ready, execution.State);
    }

    [Fact]
    public async Task UnsupportedSensationBaselineCannotBeRepairedWithoutPreflightWrites()
    {
        await using var scenario = await StockMotionScenario.ConnectAsync(
            new TestStockQualificationResolver(), 25, 50,
            TestContext.Current.CancellationToken, initialSensation: 70);
        var execution = scenario.Execution;
        await execution.InitializeAsync(execution.AuthorityRevision, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            execution.SetAsync(
                new MotionSettings(MotionDeviceState.StrokeEngineIdle, 0, 25, 50, 50, 0, MotionPattern.SimpleStroke, 0),
                execution.AuthorityRevision,
                static settings => settings.Speed == 0 && settings.Stroke <= settings.Depth,
                TestContext.Current.CancellationToken));

        Assert.Equal(["set:speed:0"], scenario.Connection.Commands); // Initialization only.
        Assert.Equal(OssmSharp.Device.Runtime.DeviceExecutionState.Ready, execution.State);
    }

    [Fact]
    public async Task QualifiedConnectionWritesSpeedThenStopThroughRuntimeOwner()
    {
        await using var scenario = await StockMotionScenario.ConnectAsync(
            new TestStockQualificationResolver(), 25, 50, TestContext.Current.CancellationToken);
        var execution = scenario.Execution;
        await execution.InitializeAsync(execution.AuthorityRevision, TestContext.Current.CancellationToken);
        scenario.Connection.AfterWrite = command =>
        {
            if (command.Setting == "speed")
                scenario.TryReport(command.Value, 25, 50);
        };

        await execution.SetAsync(
            new MotionSettings(MotionDeviceState.StrokeEngineIdle, 12, 25, 50, 50, 0, MotionPattern.SimpleStroke, 0),
            execution.AuthorityRevision,
            static settings => settings.Speed <= 12 && settings.Stroke <= settings.Depth,
            TestContext.Current.CancellationToken);
        await execution.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["set:speed:0", "set:speed:12", "set:speed:0"], scenario.Connection.Commands);
    }

    [Fact]
    public async Task QualifiedGeometryWritesEachStoppedIntermediateBeforePositiveSpeed()
    {
        await using var scenario = await StockMotionScenario.ConnectAsync(
            new TestStockQualificationResolver(), 50, 75, TestContext.Current.CancellationToken);
        var execution = scenario.Execution;
        await execution.InitializeAsync(execution.AuthorityRevision, TestContext.Current.CancellationToken);
        var stroke = 50;
        var depth = 75;
        scenario.Connection.AfterWrite = command =>
        {
            if (command.Setting == "stroke") stroke = command.Value;
            if (command.Setting == "depth") depth = command.Value;
            if (command.Setting is "stroke" or "depth" or "speed")
                scenario.TryReport(command.Setting == "speed" ? command.Value : 0, stroke, depth);
        };

        await execution.SetAsync(
            new MotionSettings(MotionDeviceState.StrokeEngineIdle, 12, 25, 50, 50, 0, MotionPattern.SimpleStroke, 0),
            execution.AuthorityRevision,
            static settings => settings.Speed <= 12 && settings.Stroke <= settings.Depth,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            ["set:speed:0", "set:speed:0", "set:speed:0", "set:stroke:25",
                "set:speed:0", "set:depth:50", "set:speed:12"],
            scenario.Connection.Commands);
        await execution.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task StopAfterIssuedZeroPreventsAnySubsequentGeometryOrSpeedCommand()
    {
        await using var scenario = await StockMotionScenario.ConnectAsync(
            new TestStockQualificationResolver(), 50, 75, TestContext.Current.CancellationToken);
        var execution = scenario.Execution;
        await execution.InitializeAsync(execution.AuthorityRevision, TestContext.Current.CancellationToken);
        Task? stopping = null;
        scenario.Connection.AfterWrite = command =>
        {
            if (command.Setting == "speed" && scenario.Connection.Commands.Count == 2)
                stopping = execution.StopAsync(TestContext.Current.CancellationToken);
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            execution.SetAsync(
                new MotionSettings(MotionDeviceState.StrokeEngineIdle, 12, 25, 50, 50, 0, MotionPattern.SimpleStroke, 0),
                execution.AuthorityRevision,
                static settings => settings.Speed <= 12 && settings.Stroke <= settings.Depth,
                TestContext.Current.CancellationToken));

        await Assert.IsAssignableFrom<Task>(stopping);
        Assert.Equal(["set:speed:0", "set:speed:0", "set:speed:0"], scenario.Connection.Commands);
    }
}
