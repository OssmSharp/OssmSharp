namespace OssmSharp.Web.Api.Tests;

/// <summary>
/// Checks exclusive device ownership and the machine-wide lock name on Windows.
/// </summary>
public sealed class DeviceOwnershipLockTests
{
    [Fact]
    public void OwnershipNameUsesTheProvisionedMachineRuntimeDirectory()
    {
        var expected = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "OssmSharp", "runtime", "device.lock")
            : "/Library/Application Support/OssmSharp/runtime/device.lock";

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
