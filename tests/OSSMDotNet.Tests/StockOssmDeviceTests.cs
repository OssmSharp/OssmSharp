using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Threading.Channels;
using FastMoq;
using FastMoq.Extensions;
using FastMoq.Providers;
using Microsoft.Extensions.Logging;
using OneOf;

namespace OSSMDotNet.Tests;

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
