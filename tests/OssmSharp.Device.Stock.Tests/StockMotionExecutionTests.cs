namespace OssmSharp.Device.Stock.Tests;

public sealed class StockMotionExecutionTests
{
    [Fact]
    public async Task ExternalGeometryChangeIsStoppedWithASpeedOnlyZero()
    {
        await using var scenario = await StockMotionScenario.ConnectAsync(
            new UnqualifiedStockMotion(), 25, 50, TestContext.Current.CancellationToken);
        await scenario.Device.InitializeAsync(TestContext.Current.CancellationToken);
        var previous = new MotionSettings(
            MotionDeviceState.StrokeEngineIdle, 0, 25, 50, 50, 0, MotionPattern.SimpleStroke, 0);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scenario.Connection.AfterWrite = command =>
        {
            if (command.Setting == "speed" && command.Value == 0)
                stopped.TrySetResult();
        };

        Assert.True(scenario.TryReport(0, 50, 75));
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        Assert.Equal(["set:speed:0", "set:speed:0"], scenario.Connection.Commands); // Initialization, then Stop.
        Assert.False(scenario.Device.IsReady);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            scenario.Device.SetAsync(previous, TestContext.Current.CancellationToken));
        Assert.Equal(["set:speed:0", "set:speed:0"], scenario.Connection.Commands);
    }

    [Theory]
    [InlineData("menu.idle")]
    [InlineData("strokeEngine.pattern")]
    public async Task StopFromNonPlayableReportAlwaysWritesSpeedOnlyZero(string state)
    {
        await using var scenario = await StockMotionScenario.ConnectAsync(
            new UnqualifiedStockMotion(), 20, 40,
            TestContext.Current.CancellationToken, initialState: state);

        await scenario.Device.EmergencyStopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["set:speed:0"], scenario.Connection.Commands);
        Assert.False(scenario.Device.IsReady);
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
    public async Task SteadyMotionStaysReadyWithoutNewChangeDrivenFeedback()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var scenario = await StockMotionScenario.ConnectAsync(
            new TestStockQualificationResolver(), 25, 50,
            TestContext.Current.CancellationToken, clock);
        var device = scenario.Device;
        await device.InitializeAsync(TestContext.Current.CancellationToken);
        scenario.Connection.AfterWrite = command =>
        {
            if (command.Setting == "speed" && command.Value != 0)
                scenario.TryReport(command.Value, 25, 50);
        };
        var moving = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        device.DeviceStateChanged += change =>
        {
            if (change.IsT0 && change.AsT0.MotionSettings.Speed == 12)
                moving.TrySetResult();
        };
        await device.SetAsync(new MotionSettings(MotionDeviceState.StrokeEngineIdle, 12, 25, 50, 50, 0, MotionPattern.SimpleStroke, 0), TestContext.Current.CancellationToken);
        await moving.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        // The firmware reports only changes, so steady motion is silent.
        clock.Advance(TimeSpan.FromSeconds(10));

        Assert.True(device.IsReady);
        Assert.Equal("set:speed:12", scenario.Connection.Commands[^1]);
    }

    [Fact]
    public async Task UnqualifiedTransitionRejectsWholePlanBeforeItsFirstZeroWrite()
    {
        await using var scenario = await StockMotionScenario.ConnectAsync(
            new UnqualifiedStockMotion(), 25, 50, TestContext.Current.CancellationToken);
        var execution = scenario.Device;
        await execution.InitializeAsync(TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            execution.SetAsync(new MotionSettings(MotionDeviceState.StrokeEngineIdle, 12, 25, 50, 50, 0, MotionPattern.SimpleStroke, 0), TestContext.Current.CancellationToken));

        Assert.Equal(["set:speed:0"], scenario.Connection.Commands); // Initialization only.
        Assert.True(execution.IsReady);
    }

    [Fact]
    public async Task QualifiedMaximumSpeedRejectsHigherMotionBeforeAnyTransitionWrite()
    {
        await using var scenario = await StockMotionScenario.ConnectAsync(
            new TestStockQualificationResolver(maximumSpeedPercent: 12), 25, 50,
            TestContext.Current.CancellationToken);
        var execution = scenario.Device;
        await execution.InitializeAsync(TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            execution.SetAsync(new MotionSettings(MotionDeviceState.StrokeEngineIdle, 13, 25, 50, 50, 0, MotionPattern.SimpleStroke, 0), TestContext.Current.CancellationToken));

        Assert.Equal(["set:speed:0"], scenario.Connection.Commands); // Initialization only.
        Assert.True(execution.IsReady);
    }

    [Fact]
    public async Task LastKnownStateOutsideTheQualifiedProfileDoesNotBlockAQualifiedCommand()
    {
        await using var scenario = await StockMotionScenario.ConnectAsync(
            new TestStockQualificationResolver(), 50, 75,
            TestContext.Current.CancellationToken, initialSensation: 70);
        var device = scenario.Device;
        await device.InitializeAsync(TestContext.Current.CancellationToken);

        await device.SetAsync(new MotionSettings(MotionDeviceState.StrokeEngineIdle, 12, 25, 50, 50, 0, MotionPattern.SimpleStroke, 0), TestContext.Current.CancellationToken);

        Assert.Equal(
            ["set:speed:0", "set:stroke:25", "set:depth:50", "set:sensation:50", "set:buffer:0", "set:pattern:0",
                "set:speed:12"],
            scenario.Connection.Commands);
    }

    [Fact]
    public async Task QualifiedCommandWritesEverySettingThenSpeedEvenWhenUnchanged()
    {
        await using var scenario = await StockMotionScenario.ConnectAsync(
            new TestStockQualificationResolver(), 25, 50, TestContext.Current.CancellationToken);
        var device = scenario.Device;
        await device.InitializeAsync(TestContext.Current.CancellationToken);

        await device.SetAsync(new MotionSettings(MotionDeviceState.StrokeEngineIdle, 12, 25, 50, 50, 0, MotionPattern.SimpleStroke, 0), TestContext.Current.CancellationToken);
        await device.EmergencyStopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            ["set:speed:0", "set:stroke:25", "set:depth:50", "set:sensation:50", "set:buffer:0", "set:pattern:0",
                "set:speed:12", "set:speed:0"],
            scenario.Connection.Commands);
    }

    [Fact]
    public async Task LargerStrokeThanTheCurrentDepthWritesDepthFirst()
    {
        await using var scenario = await StockMotionScenario.ConnectAsync(
            new TestStockQualificationResolver(), 25, 40, TestContext.Current.CancellationToken);
        var device = scenario.Device;
        await device.InitializeAsync(TestContext.Current.CancellationToken);

        await device.SetAsync(new MotionSettings(MotionDeviceState.StrokeEngineIdle, 12, 50, 50, 75, 0, MotionPattern.SimpleStroke, 0), TestContext.Current.CancellationToken);

        Assert.Equal(
            ["set:speed:0", "set:depth:75", "set:stroke:50", "set:sensation:50", "set:buffer:0", "set:pattern:0",
                "set:speed:12"],
            scenario.Connection.Commands);
    }

    [Fact]
    public async Task EmergencyStopDuringACommandPreventsItsRemainingSettings()
    {
        await using var scenario = await StockMotionScenario.ConnectAsync(
            new TestStockQualificationResolver(), 50, 75, TestContext.Current.CancellationToken);
        var device = scenario.Device;
        await device.InitializeAsync(TestContext.Current.CancellationToken);
        Task? stopping = null;
        scenario.Connection.AfterWrite = command =>
        {
            if (command.Setting == "stroke")
                stopping = device.EmergencyStopAsync(TestContext.Current.CancellationToken);
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            device.SetAsync(new MotionSettings(MotionDeviceState.StrokeEngineIdle, 12, 25, 50, 50, 0, MotionPattern.SimpleStroke, 0), TestContext.Current.CancellationToken));

        await Assert.IsAssignableFrom<Task>(stopping);
        Assert.Equal(["set:speed:0", "set:stroke:25", "set:speed:0"], scenario.Connection.Commands);
    }

}
