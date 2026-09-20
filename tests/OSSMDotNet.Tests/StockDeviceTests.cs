using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Threading.Channels;
using FastMoq;
using FastMoq.Extensions;
using OneOf;

namespace OSSMDotNet.Tests;

public sealed class StockDeviceTests : MockerTestBase<OssmDevice>, IAsyncDisposable
{
    protected override Action<Mocker> SetupMocksAction => mocks =>
    {
        mocks.SetupOptions(new OssmDeviceOptions());
        mocks.AddType<TimeProvider>(TimeProvider.System);
    };

    [Fact]
    public void ConstructorValidatesOptionsThroughDataAnnotations()
    {
        var options = new OssmDeviceOptions { ObservationLiveFor = TimeSpan.Zero };
        Mocks.SetupOptions(options, replace: true);

        Assert.IsAssignableFrom<OssmDeviceBaseOptions>(options);
        Assert.IsAssignableFrom<IValidatableObject>(options);
        var exception = Assert.Throws<TargetInvocationException>(() => Mocks.CreateInstance<OssmDevice>());
        Assert.IsType<ValidationException>(exception.InnerException);
    }

    [Fact]
    public async Task BlePlaceholderReportsFaultAndRejectsWritesWithoutClaimingConnection()
    {
        var changes = Channel.CreateUnbounded<OneOf<DeviceState, Exception>>();
        Component.DeviceStateChanged += change => changes.Writer.TryWrite(change);

        var beforeStartup = Component.GetStateAsync(TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<NoDeviceDataException>(() => beforeStartup);
        Assert.False(changes.Reader.TryRead(out _));
        Assert.Equal(DeviceConnectionState.Disconnected, Component.ConnectionState);
        Assert.Throws<InvalidOperationException>(() => Component.Device);

        await Component.StartAsync(TestContext.Current.CancellationToken);
        var reported = await changes.Reader.ReadAsync(TestContext.Current.CancellationToken);
        Assert.True(reported.IsT1);
        Assert.IsType<NotSupportedException>(reported.AsT1);

        var afterStartup = Component.GetStateAsync(TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<NoDeviceDataException>(() => afterStartup);
        Assert.False(changes.Reader.TryRead(out _));
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

        var afterFailedWrite = Component.GetStateAsync(TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<NoDeviceDataException>(() => afterFailedWrite);
        Assert.False(changes.Reader.TryRead(out _));
        Assert.Equal(DeviceConnectionState.Disconnected, Component.ConnectionState);
        Assert.Throws<InvalidOperationException>(() => Component.Device);
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
