using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Threading.Channels;

namespace OSSMDotNet.Tests;

/// <summary>
/// Checks stock device options, speed matching, and behavior when the BLE transport is unavailable.
/// </summary>
public sealed class StockOssmDeviceTests : MockerTestBase<StockOssmDevice>, IAsyncDisposable
{
    protected override Action<Mocker> SetupMocksAction => mocks =>
    {
        mocks.SetupOptions(new StockOssmDeviceOptions());
        mocks.AddType<TimeProvider>(TimeProvider.System);
        mocks.AddMethodResult<ILogger<StockOssmDevice>, bool>(
            logger => logger.IsEnabled(FastArg.Any<LogLevel>()),
            true);
    };

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    public void OptionsRejectNonPositiveExpectationLifetime(long ticks)
    {
        var options = new StockOssmDeviceOptions
        {
            ExpectationLifetime = TimeSpan.FromTicks(ticks),
        };

        var result = Assert.Single(options.Validate(new ValidationContext(options)));

        Assert.Equal("ExpectationLifetime must be positive.", result.ErrorMessage);
        Assert.Equal([nameof(StockOssmDeviceOptions.ExpectationLifetime)], result.MemberNames);
    }

    [Fact]
    public void OptionsAcceptPositiveExpectationLifetime()
    {
        var options = new StockOssmDeviceOptions
        {
            ExpectationLifetime = TimeSpan.FromTicks(1),
        };

        Assert.Empty(options.Validate(new ValidationContext(options)));
    }

    [Fact]
    public void ConstructorRejectsMissingDependencies()
    {
        var options = Options.Create(new StockOssmDeviceOptions());
        var logger = NullLogger<StockOssmDevice>.Instance;

        Assert.Throws<ArgumentNullException>(() => new StockOssmDevice(null!, TimeProvider.System, logger));
        Assert.Throws<ArgumentNullException>(() => new StockOssmDevice(options, null!, logger));
        Assert.Throws<ArgumentNullException>(() => new StockOssmDevice(options, TimeProvider.System, null!));
    }

    [Fact]
    public void ConstructorValidatesOptionsThroughDataAnnotations()
    {
        var options = new StockOssmDeviceOptions { ExpectationLifetime = TimeSpan.Zero };
        Mocks.SetupOptions(options, replace: true);

        Assert.IsAssignableFrom<StockOssmDeviceOptions>(options);
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
