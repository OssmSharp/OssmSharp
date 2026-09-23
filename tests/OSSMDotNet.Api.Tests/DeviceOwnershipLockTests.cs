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
    public void SecondLeaseCannotBeAcquiredUntilTheFirstLeaseIsReleased()
    {
        using var first = DeviceOwnershipLock.TryAcquire();

        Assert.NotNull(first);
        Assert.Null(DeviceOwnershipLock.TryAcquire());

        first.Dispose();
        using var replacement = DeviceOwnershipLock.TryAcquire();
        Assert.NotNull(replacement);
    }
}
