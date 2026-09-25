namespace OSSMDotNet.Api.Tests;

/// <summary>
/// Checks exclusive device ownership and the machine-wide lock name on Windows.
/// </summary>
public sealed class DeviceOwnershipLockTests
{
    [Fact]
    public void OwnershipNameUsesTheMachineGlobalWindowsNamespace()
    {
        var expected = OperatingSystem.IsWindows()
            ? @"Global\ossm-web-controls-device"
            : "ossm-web-controls-device";

        Assert.Equal(expected, DeviceOwnershipLock.Name);
    }

    [Fact]
    public async Task SecondLeaseCannotBeAcquiredUntilTheFirstLeaseIsReleasedFromAnotherThread()
    {
        using var first = DeviceOwnershipLock.TryAcquire();

        Assert.NotNull(first);
        Assert.Null(DeviceOwnershipLock.TryAcquire());

        await Task.Run(first.Dispose, TestContext.Current.CancellationToken);
        using var replacement = DeviceOwnershipLock.TryAcquire();
        Assert.NotNull(replacement);
    }
}
