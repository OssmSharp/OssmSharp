using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Threading.Channels;
using OssmSharp.Device.Stock.Models;

namespace OssmSharp.Device.Stock.Tests;

/// <summary>
/// Checks stock device options, speed matching, and behavior when the BLE transport is unavailable.
/// </summary>
public sealed class StockOssmDeviceTests : MockerTestBase<StockOssmDevice>, IAsyncDisposable
{
    protected override Action<Mocker> SetupMocksAction => mocks =>
    {
        mocks.SetupOptions(new DeviceRuntimeOptions());
        mocks.AddType<TimeProvider>(TimeProvider.System);
        mocks.AddMethodResult<ILogger<StockOssmDevice>, bool>(
            logger => logger.IsEnabled(FastArg.Any<LogLevel>()),
            true);
    };

    [Fact]
    public void ConstructorRejectsMissingDependencies()
    {
        var options = Options.Create(new DeviceRuntimeOptions());
        var logger = NullLogger<StockOssmDevice>.Instance;

        Assert.Throws<ArgumentNullException>(() => new StockOssmDevice(null!, TimeProvider.System, logger));
        Assert.Throws<ArgumentNullException>(() => new StockOssmDevice(options, null!, logger));
        Assert.Throws<ArgumentNullException>(() => new StockOssmDevice(options, TimeProvider.System, null!));
    }

    [Fact]
    public void ConstructorValidatesOptionsThroughDataAnnotations()
    {
        var options = new DeviceRuntimeOptions { ExpectationLifetime = TimeSpan.Zero };
        Mocks.SetupOptions(options, replace: true);

        Assert.IsAssignableFrom<DeviceRuntimeOptions>(options);
        Assert.IsAssignableFrom<IValidatableObject>(options);
        var exception = Assert.Throws<TargetInvocationException>(() => Mocks.CreateInstance<StockOssmDevice>());
        Assert.IsType<ValidationException>(exception.InnerException);
    }

    [Fact]
    public void SpeedMatchingHelperIsInternalStatic()
    {
        var method = typeof(StockOssmDevice).GetMethod(
            nameof(StockOssmDevice.IsCurrentStateCongruent),
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

        Assert.NotNull(method);
        Assert.True(method.IsAssembly);
        Assert.True(method.IsStatic);
        Assert.Equal(typeof(bool), method.ReturnType);
    }

    [Theory]
    [InlineData(100, 50, 51, 25, true)]
    [InlineData(2, 0, 51, 25, true)]
    [InlineData(2, 0, 51, 26, false)]
    public void SpeedMatchingRequiresOverlappingFractionalKnobIntervals(
        int previousRequestedSpeed,
        int previousStateSpeed,
        int requestedSpeed,
        int stateSpeed,
        bool expected)
    {
        var actual = StockOssmDevice.IsCurrentStateCongruent(
            previousRequestedSpeed,
            previousStateSpeed,
            requestedSpeed,
            stateSpeed);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task DispatchCompletionAfterDisconnectDoesNotRestoreStateMatchingContext()
    {
        await using var device = new StockSpeedTestDevice(new FakeTimeProvider(DateTimeOffset.UnixEpoch))
        {
            BlockDispatch = true,
        };
        device.Connect();
        var write = device.SetMotionAsync(Settings(40), TestContext.Current.CancellationToken);
        await device.DispatchStarted.WaitAsync(TestContext.Current.CancellationToken);

        device.Disconnect();
        device.CompleteDispatch();
        await write;
        device.Connect();
        device.ReportState(Settings(0));
        var changes = new List<OneOf<DeviceState, Exception>>();
        device.DeviceStateChanged += changes.Add;

        await device.SetMotionAsync(Settings(51), TestContext.Current.CancellationToken);
        device.ReportState(Settings(25));

        Assert.Single(changes);
        Assert.Equal(25, changes[0].AsT0.MotionSettings.Speed);
    }

    [Fact]
    public async Task StockFirmwareDepthChangePublishesSeparateSettingsError()
    {
        await using var device = new StockSpeedTestDevice(new FakeTimeProvider(DateTimeOffset.UnixEpoch));
        var initial = StockFirmwareState(1, 20, 0);
        var changed = StockFirmwareState(2, 21, 1.25);
        device.ReportState(initial);
        var changes = new List<OneOf<DeviceState, Exception>>();
        device.DeviceStateChanged += changes.Add;

        device.ReportState(changed);

        Assert.Collection(
            changes,
            change => Assert.Same(changed, change.AsT0),
            change =>
            {
                var error = Assert.IsType<UnexpectedMotionChangeException>(change.AsT1);
                Assert.Equal(initial.MotionSettings, error.PreviousSettings);
                Assert.Equal(changed.MotionSettings, error.CurrentSettings);
                Assert.Null(error.PendingRequestedSettings);
            });
    }

    [Fact]
    public async Task SettledWriteUsesPriorRequestToMatchDelayedScaledState()
    {
        await using var device = new StockSpeedTestDevice(new FakeTimeProvider(DateTimeOffset.UnixEpoch));
        var changes = new List<OneOf<DeviceState, Exception>>();
        device.DeviceStateChanged += changes.Add;

        await device.SetMotionAsync(Settings(60), TestContext.Current.CancellationToken);
        device.ReportState(Settings(30));
        await device.SetMotionAsync(Settings(40), TestContext.Current.CancellationToken);
        device.ReportState(Settings(20));

        Assert.Collection(
            changes,
            change => Assert.Equal(30, change.AsT0.MotionSettings.Speed),
            change => Assert.Equal(20, change.AsT0.MotionSettings.Speed));
    }

    [Fact]
    public async Task BlePlaceholderStaysDisconnectedLogsUnsupportedConnectionFailureAndRejectsWrites()
    {
        var changes = new List<OneOf<DeviceState, Exception>>();
        var connections = Channel.CreateUnbounded<DeviceConnectionState>();
        Component.DeviceStateChanged += changes.Add;
        Component.ConnectionStateChanged += state => connections.Writer.TryWrite(state);

        Assert.Equal(DeviceConnectionState.Disconnected, Component.ConnectionState);
        Assert.Throws<InvalidOperationException>(() => Component.Device);
        await Component.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DeviceConnectionState.Connecting, await connections.Reader.ReadAsync(TestContext.Current.CancellationToken));
        Assert.Equal(DeviceConnectionState.Disconnected, await connections.Reader.ReadAsync(TestContext.Current.CancellationToken));
        await Component.StopAsync(TestContext.Current.CancellationToken);
        Assert.Empty(changes);
        Assert.Equal(DeviceConnectionState.Disconnected, Component.ConnectionState);
        Assert.Throws<InvalidOperationException>(() => Component.Device);
        Mocks.VerifyLogged(LogLevel.Warning, "Device connection failed", TimesSpec.Once);

        var write = new MotionSettings(
            MotionDeviceState.StrokeEngineIdle,
            0,
            20,
            50,
            40,
            0,
            MotionPattern.SimpleStroke,
            0);

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            Component.SetMotionAsync(write, TestContext.Current.CancellationToken));

        Assert.Empty(changes);
        Assert.Equal(DeviceConnectionState.Disconnected, Component.ConnectionState);
    }

    private static MotionSettings Settings(int speed) => new(
        MotionDeviceState.StrokeEngineIdle,
        speed,
        20,
        50,
        40,
        0,
        MotionPattern.SimpleStroke,
        0);

    private static DeviceState StockFirmwareState(uint firmwareTimestamp, int depth, double position) =>
        StateResponse.Parse(
            $$"""
            {"timestamp":{{firmwareTimestamp}},"state":"strokeEngine.idle","speed":0,"stroke":10,"sensation":50,"depth":{{depth}},"buffer":0,"pattern":0,"position":{{position}},"sessionId":""}
            """,
            DateTime.UnixEpoch.AddSeconds(firmwareTimestamp)).ToDeviceState();

    public async ValueTask DisposeAsync()
    {
        try
        {
            await Component.DisposeAsync();
        }
        finally
        {
            base.Dispose();
        }
    }
}
