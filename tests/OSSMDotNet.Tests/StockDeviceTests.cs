using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace OSSMDotNet.Tests;

public sealed class StockDeviceTests
{
    [Fact]
    public void ConstructorValidatesOptionsThroughDataAnnotations()
    {
        var options = new OssmDeviceOptions { ObservationLiveFor = TimeSpan.Zero };

        Assert.IsAssignableFrom<OssmDeviceBaseOptions>(options);
        Assert.IsAssignableFrom<IValidatableObject>(options);
        Assert.Throws<ValidationException>(() => new OssmDevice(
            Options.Create(options),
            TimeProvider.System,
            NullLogger<OssmDevice>.Instance));
    }

    [Fact]
    public async Task BlePlaceholderReportsFaultAndRejectsWritesWithoutClaimingConnection()
    {
        await using var device = new OssmDevice(
            Options.Create(new OssmDeviceOptions()),
            TimeProvider.System,
            NullLogger<OssmDevice>.Instance);
        var fault = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        device.DeviceChanged += change => change.Switch(_ => { }, error => fault.TrySetResult(error));

        await device.StartAsync(TestContext.Current.CancellationToken);
        var reported = await fault.Task.WaitAsync(TestContext.Current.CancellationToken);
        var snapshot = await device.GetSnapshotAsync(TestContext.Current.CancellationToken);
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
            device.SetMotionAsync(write, TestContext.Current.CancellationToken));
        Assert.IsType<NotSupportedException>(reported);
        Assert.NotEqual(DeviceConnectionState.Connected, snapshot.ConnectionState);
        Assert.Equal(ObservationFreshness.Unavailable, snapshot.ObservationFreshness);
    }
}
