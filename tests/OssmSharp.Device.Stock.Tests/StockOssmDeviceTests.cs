using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Threading.Channels;
using OssmSharp.Device.Stock.Models;

namespace OssmSharp.Device.Stock.Tests;

/// <summary>
/// Checks stock device options, speed matching, and selected BLE behavior.
/// </summary>
public sealed class StockOssmDeviceTests
{
    private static readonly byte[] Identity = "a4:cf:12:34:56:78;ESP32-S3;1;0123456789abcdef0123456789abcdef;1.0.34"u8.ToArray();
    private static readonly byte[] Patterns = "[{\"name\":\"Simple Stroke\",\"idx\":0},{\"name\":\"Teasing Pounding\",\"idx\":1}]"u8.ToArray();

    [Fact]
    public void ConstructorRejectsMissingDependencies()
    {
        var options = Options.Create(new DeviceRuntimeOptions());
        var logger = NullLogger<StockOssmDevice>.Instance;
        var selected = new SelectedStockTestIdentifier("selected-peripheral");
        using var mocks = new Mocker();
        var transport = mocks.GetRequiredObject<IDeviceTransport>();

        Assert.Throws<ArgumentNullException>(() => new StockOssmDevice(null!, TimeProvider.System, logger, selected, transport));
        Assert.Throws<ArgumentNullException>(() => new StockOssmDevice(options, null!, logger, selected, transport));
        Assert.Throws<ArgumentNullException>(() => new StockOssmDevice(options, TimeProvider.System, null!, selected, transport));
        Assert.Throws<ArgumentNullException>(() => new StockOssmDevice(options, TimeProvider.System, logger, null!, transport));
        Assert.Throws<ArgumentNullException>(() => new StockOssmDevice(options, TimeProvider.System, logger, selected, null!));
        Assert.Empty(typeof(StockOssmDevice).GetConstructors(BindingFlags.Public | BindingFlags.Instance));
    }

    [Fact]
    public void ConstructorValidatesOptionsThroughDataAnnotations()
    {
        var options = new DeviceRuntimeOptions { ExpectationLifetime = TimeSpan.Zero };

        Assert.IsAssignableFrom<DeviceRuntimeOptions>(options);
        Assert.IsAssignableFrom<IValidatableObject>(options);
        Assert.Throws<ValidationException>(() => new StockOssmDevice(
            Options.Create(options), TimeProvider.System, NullLogger<StockOssmDevice>.Instance,
            new SelectedStockTestIdentifier("selected-peripheral"), null!));
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
    public async Task MacOSConstructorAcceptsInjectedSelectedDeviceWithoutBluetoothAccess()
    {
        using var mocks = new Mocker();
        await using var device = new StockOssmDevice(
            Options.Create(new DeviceRuntimeOptions()),
            TimeProvider.System,
            NullLogger<StockOssmDevice>.Instance,
            new SelectedStockTestIdentifier("selected-peripheral"),
            mocks.GetRequiredObject<IDeviceTransport>());

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
    public async Task InjectedTransportFailureLeavesDeviceDisconnectedAndCannotWrite()
    {
        var transport = new UnavailableTransport();
        await using var device = new StockOssmDevice(
            Options.Create(new DeviceRuntimeOptions()), TimeProvider.System,
            NullLogger<StockOssmDevice>.Instance,
            new SelectedStockTestIdentifier("selected-peripheral"), transport);
        var connections = Channel.CreateUnbounded<DeviceConnectionState>();
        var changes = new List<OneOf<DeviceState, Exception>>();
        device.ConnectionStateChanged += state => connections.Writer.TryWrite(state);
        device.DeviceStateChanged += changes.Add;

        await device.StartAsync(TestContext.Current.CancellationToken);
        Assert.Equal(
            DeviceConnectionState.Connecting,
            await connections.Reader.ReadAsync(TestContext.Current.CancellationToken));
        Assert.Equal(
            DeviceConnectionState.Disconnected,
            await connections.Reader.ReadAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, transport.ConnectCalls);
        Assert.Empty(changes);
        Assert.Equal(DeviceConnectionState.Disconnected, device.ConnectionState);
        Assert.Throws<InvalidOperationException>(() => device.Device);
        await Assert.ThrowsAsync<IOException>(
            () => device.SetMotionAsync(Settings(0), TestContext.Current.CancellationToken));
        Assert.Equal(1, transport.ConnectCalls);
    }

    [Fact]
    public async Task ConnectSelectsPeripheralIdAndVerifiesBeforePublishing()
    {
        using var mocks = new Mocker();
        var notifications = Channel.CreateUnbounded<byte[]>();
        mocks.AddMethodResultAsync<IDeviceConnection, StockGattProfile>(
            ble => ble.ReadProfileAsync(FastArg.Any<CancellationToken>()),
            new StockGattProfile(
                "enrolled", StockFirmwareProfile.Service,
                [StockFirmwareProfile.Identity, StockFirmwareProfile.Command, StockFirmwareProfile.KnobConfiguration,
                    StockFirmwareProfile.State, StockFirmwareProfile.Patterns, StockFirmwareProfile.PatternData],
                Identity, Patterns, notifications.Reader));
        var connection = mocks.GetRequiredObject<IDeviceConnection>();
        mocks.AddMethodResultAsync<IDeviceTransport, IDeviceConnection>(
            ble => ble.ConnectAsync(FastArg.Any<DeviceIdentifier>(), StockFirmwareProfile.Service, FastArg.Any<CancellationToken>()),
            connection);
        var transport = mocks.GetRequiredObject<IDeviceTransport>();
        await using var device = new StockOssmDevice(
            Options.Create(new DeviceRuntimeOptions()), TimeProvider.System,
            NullLogger<StockOssmDevice>.Instance, new SelectedStockTestIdentifier("enrolled"), transport);
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
        mocks.Verify<IDeviceTransport>(
            ble => ble.ConnectAsync(FastArg.Any<DeviceIdentifier>(), StockFirmwareProfile.Service, FastArg.Any<CancellationToken>()),
            TimesSpec.Once);
        await device.StopAsync(TestContext.Current.CancellationToken);
        mocks.Verify<IDeviceConnection>(ble => ble.DisposeAsync(), TimesSpec.Once);
    }

    [Fact]
    public async Task ConnectedStockDispatchesOnlyZeroSpeedWithoutVerifiedMotionReadiness()
    {
        using var mocks = new Mocker();
        var notifications = Channel.CreateUnbounded<byte[]>();
        await using var connection = new RecordingStockConnection(
            new StockGattProfile(
                "enrolled", StockFirmwareProfile.Service,
                [StockFirmwareProfile.Identity, StockFirmwareProfile.Command, StockFirmwareProfile.KnobConfiguration,
                    StockFirmwareProfile.State, StockFirmwareProfile.Patterns, StockFirmwareProfile.PatternData],
                Identity, Patterns, notifications.Reader));
        mocks.AddMethodResultAsync<IDeviceTransport, IDeviceConnection>(
            ble => ble.ConnectAsync(FastArg.Any<DeviceIdentifier>(), StockFirmwareProfile.Service, FastArg.Any<CancellationToken>()),
            connection);
        await using var device = new StockOssmDevice(
            Options.Create(new DeviceRuntimeOptions()), TimeProvider.System,
            NullLogger<StockOssmDevice>.Instance, new SelectedStockTestIdentifier("enrolled"),
            mocks.GetRequiredObject<IDeviceTransport>());
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        device.ConnectionStateChanged += state =>
        {
            if (state == DeviceConnectionState.Connected)
                connected.TrySetResult();
        };

        await device.StartAsync(TestContext.Current.CancellationToken);
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => device.SetMotionAsync(Settings(12), TestContext.Current.CancellationToken));
        Assert.Empty(connection.Commands);

        await device.SetMotionAsync(Settings(0), TestContext.Current.CancellationToken);
        Assert.Equal(["set:speed:0"], connection.Commands);
    }

    [Fact]
    public async Task UnqualifiedStoppedGeometryNeverWritesEvenAfterKnobPreparation()
    {
        await using var scenario = await StockMotionScenario.ConnectAsync(
            new UnqualifiedStockMotion(), 20, 40, TestContext.Current.CancellationToken);
        await scenario.Device.PrepareAsync(
            TestContext.Current.CancellationToken, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => scenario.Device.SetMotionAsync(
                Settings(0) with { Stroke = 10 }, TestContext.Current.CancellationToken));

        Assert.Empty(scenario.Connection.Commands);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task VerifiedStoppedFieldWriteChecksRevocationAndFreshness(
        bool revokeAfterZero,
        bool stale)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var mocks = new Mocker();
        var notifications = Channel.CreateUnbounded<byte[]>();
        await using var connection = new RecordingStockConnection(
            new StockGattProfile(
                "enrolled", StockFirmwareProfile.Service,
                [StockFirmwareProfile.Identity, StockFirmwareProfile.Command, StockFirmwareProfile.KnobConfiguration,
                    StockFirmwareProfile.State, StockFirmwareProfile.Patterns, StockFirmwareProfile.PatternData],
                Identity, Patterns, notifications.Reader));
        mocks.AddMethodResultAsync<IDeviceTransport, IDeviceConnection>(
            ble => ble.ConnectAsync(FastArg.Any<DeviceIdentifier>(), StockFirmwareProfile.Service, FastArg.Any<CancellationToken>()),
            connection);
        await using var device = new StockOssmDevice(
            Options.Create(new DeviceRuntimeOptions()), clock,
            NullLogger<StockOssmDevice>.Instance, new SelectedStockTestIdentifier("enrolled"),
            mocks.GetRequiredObject<IDeviceTransport>(), new TestStockQualificationResolver());
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reported = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        device.ConnectionStateChanged += state =>
        {
            if (state == DeviceConnectionState.Connected)
                connected.TrySetResult();
        };
        device.DeviceStateChanged += change =>
        {
            if (change.IsT0)
                reported.TrySetResult();
        };
        await device.StartAsync(TestContext.Current.CancellationToken);
        await connected.Task.WaitAsync(TestContext.Current.CancellationToken);
        await notifications.Writer.WriteAsync(
            """{"timestamp":1,"state":"strokeEngine.idle","speed":0,"stroke":50,"sensation":50,"depth":75,"buffer":0,"pattern":0,"position":0,"sessionId":""}"""u8.ToArray(),
            TestContext.Current.CancellationToken);
        await reported.Task.WaitAsync(TestContext.Current.CancellationToken);
        await device.PrepareAsync(
            TestContext.Current.CancellationToken, TestContext.Current.CancellationToken);
        if (stale)
            clock.Advance(TimeSpan.FromSeconds(6));

        using var revocation = new CancellationTokenSource();
        if (revokeAfterZero)
            connection.AfterWrite = command =>
            {
                if (command.Setting == "speed")
                    revocation.Cancel();
            };

        var requested = Settings(0) with { Stroke = 25, Depth = 75 };
        if (stale)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => device.SetMotionAsync(requested, revocation.Token));
            Assert.Empty(connection.Commands);
        }
        else if (revokeAfterZero)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => device.SetMotionAsync(requested, revocation.Token));
            Assert.Equal(["set:speed:0"], connection.Commands);
        }
        else
        {
            await device.SetMotionAsync(requested, revocation.Token);
            Assert.Equal(["set:speed:0", "set:stroke:25"], connection.Commands);
        }
    }

    [Fact]
    public async Task DifferentConnectedPeripheralNeverPublishesConnectedAndReleasesConnection()
    {
        using var mocks = new Mocker();
        mocks.AddMethodResultAsync<IDeviceConnection, StockGattProfile>(
            ble => ble.ReadProfileAsync(FastArg.Any<CancellationToken>()),
            new StockGattProfile(
                "different-peripheral", StockFirmwareProfile.Service,
                [StockFirmwareProfile.Identity, StockFirmwareProfile.Command, StockFirmwareProfile.KnobConfiguration,
                    StockFirmwareProfile.State, StockFirmwareProfile.Patterns, StockFirmwareProfile.PatternData],
                Identity, Patterns, Channel.CreateUnbounded<byte[]>().Reader));
        var connection = mocks.GetRequiredObject<IDeviceConnection>();
        mocks.AddMethodResultAsync<IDeviceTransport, IDeviceConnection>(
            ble => ble.ConnectAsync(FastArg.Any<DeviceIdentifier>(), StockFirmwareProfile.Service, FastArg.Any<CancellationToken>()),
            connection);
        await using var device = new StockOssmDevice(
            Options.Create(new DeviceRuntimeOptions()), TimeProvider.System,
            NullLogger<StockOssmDevice>.Instance, new SelectedStockTestIdentifier("enrolled"),
            mocks.GetRequiredObject<IDeviceTransport>());
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
        mocks.Verify<IDeviceConnection>(ble => ble.DisposeAsync(), TimesSpec.Once);
    }

    [Fact]
    public async Task UnsettledDisconnectAfterFailedProfileReadFailsDeviceDisposal()
    {
        using var mocks = new Mocker();
        await using var connection = new UnsettledProfileConnection();
        mocks.AddMethodResultAsync<IDeviceTransport, IDeviceConnection>(
            ble => ble.ConnectAsync(FastArg.Any<DeviceIdentifier>(), StockFirmwareProfile.Service, FastArg.Any<CancellationToken>()),
            connection);
        using var device = new StockOssmDevice(
            Options.Create(new DeviceRuntimeOptions()), TimeProvider.System,
            NullLogger<StockOssmDevice>.Instance, new SelectedStockTestIdentifier("selected"),
            mocks.GetRequiredObject<IDeviceTransport>());
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
        mocks.AddMethodResultAsync<IDeviceConnection, StockGattProfile>(
            ble => ble.ReadProfileAsync(FastArg.Any<CancellationToken>()),
            new StockGattProfile(
                "enrolled", StockFirmwareProfile.Service,
                [StockFirmwareProfile.Identity, StockFirmwareProfile.Command, StockFirmwareProfile.KnobConfiguration,
                    StockFirmwareProfile.State, StockFirmwareProfile.Patterns, StockFirmwareProfile.PatternData],
                Identity, Patterns, notifications.Reader));
        var connection = mocks.GetRequiredObject<IDeviceConnection>();
        mocks.AddMethodResultAsync<IDeviceTransport, IDeviceConnection>(
            ble => ble.ConnectAsync(FastArg.Any<DeviceIdentifier>(), StockFirmwareProfile.Service, FastArg.Any<CancellationToken>()),
            connection);
        await using var device = new StockOssmDevice(
            Options.Create(new DeviceRuntimeOptions()), TimeProvider.System,
            NullLogger<StockOssmDevice>.Instance, new SelectedStockTestIdentifier("enrolled"),
            mocks.GetRequiredObject<IDeviceTransport>());
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
        mocks.AddMethodResultAsync<IDeviceConnection, StockGattProfile>(
            ble => ble.ReadProfileAsync(FastArg.Any<CancellationToken>()),
            new StockGattProfile(
                "enrolled", StockFirmwareProfile.Service,
                [StockFirmwareProfile.Identity, StockFirmwareProfile.Command, StockFirmwareProfile.KnobConfiguration,
                    StockFirmwareProfile.State, StockFirmwareProfile.Patterns, StockFirmwareProfile.PatternData],
                Identity, Patterns, notifications.Reader));
        var connection = mocks.GetRequiredObject<IDeviceConnection>();
        mocks.AddMethodResultAsync<IDeviceTransport, IDeviceConnection>(
            ble => ble.ConnectAsync(FastArg.Any<DeviceIdentifier>(), StockFirmwareProfile.Service, FastArg.Any<CancellationToken>()),
            connection);
        await using var device = new StockOssmDevice(
            Options.Create(new DeviceRuntimeOptions()), TimeProvider.System,
            NullLogger<StockOssmDevice>.Instance, new SelectedStockTestIdentifier("enrolled"),
            mocks.GetRequiredObject<IDeviceTransport>());
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
        mocks.Verify<IDeviceConnection>(ble => ble.DisposeAsync(), TimesSpec.Once);
    }

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
    public void VerificationAcceptsDifferentReportedFirmwareVersion(string version)
    {
        var identity = System.Text.Encoding.UTF8.GetBytes(
            $"a4:cf:12:34:56:78;ESP32-S3;1;0123456789abcdef0123456789abcdef;{version}");

        var device = StockFirmwareProfile.Verify(
            "selected-peripheral", StockFirmwareProfile.Service,
            [StockFirmwareProfile.Identity, StockFirmwareProfile.Command, StockFirmwareProfile.KnobConfiguration,
                StockFirmwareProfile.State, StockFirmwareProfile.Patterns, StockFirmwareProfile.PatternData],
            identity, Patterns);

        Assert.Equal(version, device.Version);
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

    private sealed class UnavailableTransport : IDeviceTransport
    {
        public int ConnectCalls { get; private set; }

        public Task<IDeviceConnection> ConnectAsync(
            DeviceIdentifier selected,
            Guid service,
            CancellationToken cancellationToken)
        {
            ConnectCalls++;
            throw new NotSupportedException("Native BLE service unavailable.");
        }
    }

    private sealed class UnsettledProfileConnection : IDeviceConnection
    {
        public bool DisposalAttempted { get; private set; }

        public Task<StockGattProfile> ReadProfileAsync(CancellationToken cancellationToken) =>
            Task.FromException<StockGattProfile>(new InvalidDataException("Invalid stock identity."));

        public Task WriteCommandAsync(Request command, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Profile failed before motion could be written.");

        public Task EnableKnobLimitingAsync(
            CancellationToken revocationToken,
            CancellationToken settlementToken) =>
            throw new InvalidOperationException("Profile failed before configuration.");

        public ValueTask DisposeAsync()
        {
            if (DisposalAttempted)
                return ValueTask.CompletedTask;

            DisposalAttempted = true;
            return ValueTask.FromException(new TimeoutException("Stock BLE disconnection did not settle."));
        }
    }

}
