namespace OSSMDotNet.ControlService.Tests;

public sealed class DeviceOwnershipLockTests
{
    [Fact]
    public void OwnershipNameUsesTheMachineGlobalWindowsNamespace()
    {
        var expected = OperatingSystem.IsWindows()
            ? @"Global\ossm-web-controls-prototype-device"
            : "ossm-web-controls-prototype-device";

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
