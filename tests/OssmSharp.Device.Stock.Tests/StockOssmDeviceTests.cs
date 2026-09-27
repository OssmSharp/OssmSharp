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
    private static readonly byte[] Identity = "a4:cf:12:34:56:78;ESP32-S3;1;0123456789abcdef0123456789abcdef;1.0.34"u8.ToArray();
    private static readonly byte[] Patterns = "[{\"name\":\"Simple Stroke\",\"idx\":0},{\"name\":\"Teasing Pounding\",\"idx\":1}]"u8.ToArray();

    protected override Type[] ComponentConstructorParameterTypes =>
        [typeof(IOptions<DeviceRuntimeOptions>), typeof(TimeProvider), typeof(ILogger<StockOssmDevice>)];

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

        Assert.IsAssignableFrom<DeviceRuntimeOptions>(options);
        Assert.IsAssignableFrom<IValidatableObject>(options);
        Assert.Throws<ValidationException>(() => new StockOssmDevice(
            Options.Create(options), TimeProvider.System, NullLogger<StockOssmDevice>.Instance));
    }

    [Fact]
    public void StockBleOptionsRequiresOnlySelectedPeripheralId()
    {
        var options = new StockBleOptions { PeripheralId = "selected-peripheral" };

        Validator.ValidateObject(options, new ValidationContext(options), validateAllProperties: true);
        Assert.Null(typeof(StockBleOptions).GetProperty("ExpectedWifiMac"));
    }

#if MACOS
    [Fact]
    public async Task MacOSConstructorAcceptsSelectedIdWithoutWifiMacOrBluetoothAccess()
    {
        using var mocks = new Mocker();
        await using var device = new StockOssmDevice(
            Options.Create(new DeviceRuntimeOptions()),
            Options.Create(new StockBleOptions { PeripheralId = "selected-peripheral" }),
            mocks.GetRequiredObject<Shiny.BluetoothLE.IBleManager>(),
            TimeProvider.System,
            NullLogger<StockOssmDevice>.Instance);

        Assert.Equal(DeviceConnectionState.Disconnected, device.ConnectionState);
        Assert.Throws<InvalidOperationException>(() => device.Device);
    }
#endif

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

    [Fact]
    public async Task ConnectSelectsPeripheralIdAndVerifiesBeforePublishing()
    {
        using var mocks = new Mocker();
        var notifications = Channel.CreateUnbounded<byte[]>();
        mocks.AddMethodResultAsync<IStockBleConnection, StockGattProfile>(
            ble => ble.ReadProfileAsync(FastArg.Any<CancellationToken>()),
            new StockGattProfile(
                "enrolled", StockFirmwareProfile.Service,
                [StockFirmwareProfile.Identity, StockFirmwareProfile.Command, StockFirmwareProfile.KnobConfiguration,
                    StockFirmwareProfile.State, StockFirmwareProfile.Patterns, StockFirmwareProfile.PatternData],
                Identity, Patterns, notifications.Reader));
        var connection = mocks.GetRequiredObject<IStockBleConnection>();
        mocks.AddMethodResultAsync<IStockBleTransport, IStockBleConnection>(
            ble => ble.ConnectAsync("enrolled", StockFirmwareProfile.Service, FastArg.Any<CancellationToken>()),
            connection);
        var transport = mocks.GetRequiredObject<IStockBleTransport>();
        await using var device = new StockOssmDevice(
            Options.Create(new DeviceRuntimeOptions()), TimeProvider.System,
            NullLogger<StockOssmDevice>.Instance, "enrolled", transport);
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        device.ConnectionStateChanged += state =>
        {
            if (state == DeviceConnectionState.Connected)
            {
                connected.TrySetResult();
            }
        };
        var states = new List<DeviceState>();
        device.DeviceStateChanged += change =>
        {
            if (change.IsT0)
            {
                states.Add(change.AsT0);
            }
        };

        await device.StartAsync(TestContext.Current.CancellationToken);
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        Assert.Equal("enrolled", device.Device.Identity);
        Assert.Equal("1.0.34", device.Device.Version);
        Assert.Empty(states);
        mocks.Verify<IStockBleTransport>(
            ble => ble.ConnectAsync("enrolled", StockFirmwareProfile.Service, FastArg.Any<CancellationToken>()),
            TimesSpec.Once);
        await device.StopAsync(TestContext.Current.CancellationToken);
        mocks.Verify<IStockBleConnection>(ble => ble.DisposeAsync(), TimesSpec.Once);
    }

    [Fact]
    public async Task DifferentConnectedPeripheralNeverPublishesConnectedAndReleasesConnection()
    {
        using var mocks = new Mocker();
        mocks.AddMethodResultAsync<IStockBleConnection, StockGattProfile>(
            ble => ble.ReadProfileAsync(FastArg.Any<CancellationToken>()),
            new StockGattProfile(
                "different-peripheral", StockFirmwareProfile.Service,
                [StockFirmwareProfile.Identity, StockFirmwareProfile.Command, StockFirmwareProfile.KnobConfiguration,
                    StockFirmwareProfile.State, StockFirmwareProfile.Patterns, StockFirmwareProfile.PatternData],
                Identity, Patterns, Channel.CreateUnbounded<byte[]>().Reader));
        var connection = mocks.GetRequiredObject<IStockBleConnection>();
        mocks.AddMethodResultAsync<IStockBleTransport, IStockBleConnection>(
            ble => ble.ConnectAsync("enrolled", StockFirmwareProfile.Service, FastArg.Any<CancellationToken>()),
            connection);
        await using var device = new StockOssmDevice(
            Options.Create(new DeviceRuntimeOptions()), TimeProvider.System,
            NullLogger<StockOssmDevice>.Instance, "enrolled",
            mocks.GetRequiredObject<IStockBleTransport>());
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connections = new List<DeviceConnectionState>();
        device.ConnectionStateChanged += state =>
        {
            connections.Add(state);
            if (state == DeviceConnectionState.Disconnected)
            {
                disconnected.TrySetResult();
            }
        };

        await device.StartAsync(TestContext.Current.CancellationToken);
        await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        Assert.Equal([DeviceConnectionState.Connecting, DeviceConnectionState.Disconnected], connections);
        Assert.Throws<InvalidOperationException>(() => device.Device);
        mocks.Verify<IStockBleConnection>(ble => ble.DisposeAsync(), TimesSpec.Once);
    }

    [Fact]
    public async Task UnsettledDisconnectAfterFailedProfileReadFailsDeviceDisposal()
    {
        using var mocks = new Mocker();
        await using var connection = new UnsettledProfileConnection();
        mocks.AddMethodResultAsync<IStockBleTransport, IStockBleConnection>(
            ble => ble.ConnectAsync("selected", StockFirmwareProfile.Service, FastArg.Any<CancellationToken>()),
            connection);
        using var device = new StockOssmDevice(
            Options.Create(new DeviceRuntimeOptions()), TimeProvider.System,
            NullLogger<StockOssmDevice>.Instance, "selected",
            mocks.GetRequiredObject<IStockBleTransport>());
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        device.ConnectionStateChanged += state =>
        {
            if (state == DeviceConnectionState.Disconnected)
                disconnected.TrySetResult();
        };

        await device.StartAsync(TestContext.Current.CancellationToken);
        await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        var failure = await Assert.ThrowsAsync<TimeoutException>(() => device.DisposeAsync().AsTask());
        Assert.Contains("did not settle", failure.Message, StringComparison.Ordinal);
        Assert.True(connection.DisposalAttempted);
    }

    [Fact]
    public async Task DisconnectedDuringProfileReadNeverPublishesConnected()
    {
        using var mocks = new Mocker();
        var notifications = Channel.CreateUnbounded<byte[]>();
        notifications.Writer.TryComplete(new IOException("Connection lost during identification."));
        mocks.AddMethodResultAsync<IStockBleConnection, StockGattProfile>(
            ble => ble.ReadProfileAsync(FastArg.Any<CancellationToken>()),
            new StockGattProfile(
                "enrolled", StockFirmwareProfile.Service,
                [StockFirmwareProfile.Identity, StockFirmwareProfile.Command, StockFirmwareProfile.KnobConfiguration,
                    StockFirmwareProfile.State, StockFirmwareProfile.Patterns, StockFirmwareProfile.PatternData],
                Identity, Patterns, notifications.Reader));
        var connection = mocks.GetRequiredObject<IStockBleConnection>();
        mocks.AddMethodResultAsync<IStockBleTransport, IStockBleConnection>(
            ble => ble.ConnectAsync("enrolled", StockFirmwareProfile.Service, FastArg.Any<CancellationToken>()),
            connection);
        await using var device = new StockOssmDevice(
            Options.Create(new DeviceRuntimeOptions()), TimeProvider.System,
            NullLogger<StockOssmDevice>.Instance, "enrolled",
            mocks.GetRequiredObject<IStockBleTransport>());
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connections = new List<DeviceConnectionState>();
        device.ConnectionStateChanged += state =>
        {
            connections.Add(state);
            if (state == DeviceConnectionState.Disconnected)
            {
                disconnected.TrySetResult();
            }
        };

        await device.StartAsync(TestContext.Current.CancellationToken);
        await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        Assert.Equal([DeviceConnectionState.Connecting, DeviceConnectionState.Disconnected], connections);
        Assert.Throws<InvalidOperationException>(() => device.Device);
    }

    [Fact]
    public async Task StockNotificationPublishesParsedStateAndChannelCompletionDisconnects()
    {
        using var mocks = new Mocker();
        var notifications = Channel.CreateUnbounded<byte[]>();
        mocks.AddMethodResultAsync<IStockBleConnection, StockGattProfile>(
            ble => ble.ReadProfileAsync(FastArg.Any<CancellationToken>()),
            new StockGattProfile(
                "enrolled", StockFirmwareProfile.Service,
                [StockFirmwareProfile.Identity, StockFirmwareProfile.Command, StockFirmwareProfile.KnobConfiguration,
                    StockFirmwareProfile.State, StockFirmwareProfile.Patterns, StockFirmwareProfile.PatternData],
                Identity, Patterns, notifications.Reader));
        var connection = mocks.GetRequiredObject<IStockBleConnection>();
        mocks.AddMethodResultAsync<IStockBleTransport, IStockBleConnection>(
            ble => ble.ConnectAsync("enrolled", StockFirmwareProfile.Service, FastArg.Any<CancellationToken>()),
            connection);
        await using var device = new StockOssmDevice(
            Options.Create(new DeviceRuntimeOptions()), TimeProvider.System,
            NullLogger<StockOssmDevice>.Instance, "enrolled",
            mocks.GetRequiredObject<IStockBleTransport>());
        var received = new TaskCompletionSource<DeviceState>(TaskCreationOptions.RunContinuationsAsynchronously);
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        device.DeviceStateChanged += change =>
        {
            if (change.IsT0)
            {
                received.TrySetResult(change.AsT0);
            }
        };
        device.ConnectionStateChanged += state =>
        {
            if (state == DeviceConnectionState.Disconnected)
            {
                disconnected.TrySetResult();
            }
        };

        await device.StartAsync(TestContext.Current.CancellationToken);
        await notifications.Writer.WriteAsync(
            "{\"timestamp\":1,\"state\":\"strokeEngine.idle\",\"speed\":0,\"stroke\":20,\"sensation\":50,\"depth\":40,\"buffer\":0,\"pattern\":0,\"position\":0,\"sessionId\":\"\"}"u8.ToArray(),
            TestContext.Current.CancellationToken);
        var state = await received.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        notifications.Writer.TryComplete();
        await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        Assert.Equal(MotionDeviceState.StrokeEngineIdle, state.MotionSettings.State);
        Assert.Equal(DateTimeKind.Utc, state.Timestamp.Kind);
        Assert.Null(state.VelocityMillimetresPerSecond);
        mocks.Verify<IStockBleConnection>(ble => ble.DisposeAsync(), TimesSpec.Once);
    }

#if WINDOWS || MACOS
    [Theory]
    [InlineData("enrolled", true, true)]
    [InlineData("ENROLLED", true, true)]
    [InlineData("another-device", true, false)]
    [InlineData("enrolled", false, false)]
    public void NativeScanSelectsOnlyTheExactLocatorAdvertisingTheService(
        string seenLocator, bool advertisesService, bool expected)
    {
        var services = advertisesService ? new[] { StockFirmwareProfile.Service.ToString() } : Array.Empty<string>();

        Assert.Equal(expected, StockBleDiscovery.Matches(
            seenLocator, services, "enrolled", StockFirmwareProfile.Service));
    }
#endif

    [Fact]
    public void VerifiedGattSurfaceReportsIdentityAndOnlyReportedPatterns()
    {
        var device = StockFirmwareProfile.Verify(
            "selected-peripheral",
            StockFirmwareProfile.Service,
            [StockFirmwareProfile.Identity, StockFirmwareProfile.Command, StockFirmwareProfile.KnobConfiguration,
                StockFirmwareProfile.State, StockFirmwareProfile.Patterns, StockFirmwareProfile.PatternData],
            Identity,
            Patterns);

        Assert.Equal("selected-peripheral", device.Identity);
        Assert.Equal("1.0.34", device.Version);
        Assert.Equal([MotionPattern.SimpleStroke], device.SupportedPatterns);
    }

    [Theory]
    [InlineData("a4:cf:12:34:56:78", "A4:CF:12:34:56:78")]
    [InlineData("00:11:22:33:44:55", "00:11:22:33:44:55")]
    public void VerificationAcceptsDifferentReportedWifiMacWithoutEnrollment(string reportedMac, string normalizedMac)
    {
        var reportedIdentity = System.Text.Encoding.UTF8.GetBytes(
            $"{reportedMac};ESP32-S3;1;0123456789abcdef0123456789abcdef;1.0.34");
        var device = StockFirmwareProfile.Verify(
            "selected-peripheral",
            StockFirmwareProfile.Service,
            [StockFirmwareProfile.Identity, StockFirmwareProfile.Command, StockFirmwareProfile.KnobConfiguration,
                StockFirmwareProfile.State, StockFirmwareProfile.Patterns, StockFirmwareProfile.PatternData],
            reportedIdentity,
            Patterns);

        Assert.Equal("selected-peripheral", device.Identity);
        Assert.Equal("1.0.34", device.Version);
        Assert.Equal(normalizedMac, PairingIdentity.Parse(reportedIdentity).WifiMac);
    }

    [Theory]
    [InlineData("1.0.35")]
    [InlineData("1.0.33")]
    public void VerificationRejectsUnsupportedFirmware(string version)
    {
        var identity = System.Text.Encoding.UTF8.GetBytes(
            $"a4:cf:12:34:56:78;ESP32-S3;1;0123456789abcdef0123456789abcdef;{version}");

        Assert.Throws<InvalidDataException>(() => StockFirmwareProfile.Verify(
            "selected-peripheral", StockFirmwareProfile.Service,
            [StockFirmwareProfile.Identity, StockFirmwareProfile.Command, StockFirmwareProfile.KnobConfiguration,
                StockFirmwareProfile.State, StockFirmwareProfile.Patterns, StockFirmwareProfile.PatternData],
            identity, Patterns));
    }

    [Fact]
    public void VerificationRejectsMissingRequiredGattCharacteristic()
    {
        Assert.Throws<InvalidDataException>(() => StockFirmwareProfile.Verify(
            "selected-peripheral", StockFirmwareProfile.Service,
            [StockFirmwareProfile.Identity, StockFirmwareProfile.Command, StockFirmwareProfile.State,
                StockFirmwareProfile.Patterns, StockFirmwareProfile.PatternData],
            Identity, Patterns));
    }

    [Fact]
    public void VerificationRejectsMissingSimpleStroke()
    {
        Assert.Throws<InvalidDataException>(() => StockFirmwareProfile.Verify(
            "selected-peripheral", StockFirmwareProfile.Service,
            [StockFirmwareProfile.Identity, StockFirmwareProfile.Command, StockFirmwareProfile.KnobConfiguration,
                StockFirmwareProfile.State, StockFirmwareProfile.Patterns, StockFirmwareProfile.PatternData],
            Identity, "[{\"name\":\"Other\",\"idx\":1}]"u8.ToArray()));
    }

    [Fact]
    public void VerificationRejectsWrongServiceAndOversizedPayloads()
    {
        var characteristics = new[] { StockFirmwareProfile.Identity, StockFirmwareProfile.Command,
            StockFirmwareProfile.KnobConfiguration, StockFirmwareProfile.State,
            StockFirmwareProfile.Patterns, StockFirmwareProfile.PatternData };

        Assert.Throws<InvalidDataException>(() => StockFirmwareProfile.Verify(
            "selected-peripheral", Guid.Empty,
            characteristics, Identity, Patterns));
        Assert.Throws<InvalidDataException>(() => StockFirmwareProfile.Verify(
            "selected-peripheral", StockFirmwareProfile.Service,
            characteristics, new byte[257], Patterns));
        Assert.Throws<InvalidDataException>(() => StockFirmwareProfile.Verify(
            "selected-peripheral", StockFirmwareProfile.Service,
            characteristics, Identity, new byte[4097]));
    }

    [Fact]
    public void VerificationRejectsMalformedCharacteristicData()
    {
        var characteristics = new[] { StockFirmwareProfile.Identity, StockFirmwareProfile.Command,
            StockFirmwareProfile.KnobConfiguration, StockFirmwareProfile.State,
            StockFirmwareProfile.Patterns, StockFirmwareProfile.PatternData };

        Assert.Throws<InvalidDataException>(() => StockFirmwareProfile.Verify(
            "selected-peripheral", StockFirmwareProfile.Service,
            characteristics, [0xff], Patterns));
        Assert.Throws<InvalidDataException>(() => StockFirmwareProfile.Verify(
            "selected-peripheral", StockFirmwareProfile.Service,
            characteristics, Identity, "{}"u8.ToArray()));
    }

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

    private sealed class UnsettledProfileConnection : IStockBleConnection
    {
        public bool DisposalAttempted { get; private set; }

        public Task<StockGattProfile> ReadProfileAsync(CancellationToken cancellationToken) =>
            Task.FromException<StockGattProfile>(new InvalidDataException("Invalid stock identity."));

        public ValueTask DisposeAsync()
        {
            if (DisposalAttempted)
                return ValueTask.CompletedTask;

            DisposalAttempted = true;
            return ValueTask.FromException(new TimeoutException("Stock BLE disconnection did not settle."));
        }
    }

}
