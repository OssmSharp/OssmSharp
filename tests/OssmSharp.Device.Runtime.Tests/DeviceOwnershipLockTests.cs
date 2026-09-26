using OssmSharp.Device.Runtime;

namespace OssmSharp.Device.Runtime.Tests;

public sealed class DeviceOwnershipLockTests
{
    [Fact]
    public void OwnershipPathUsesTheProvisionedMachineRuntimeDirectory()
    {
        var expected = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "OssmSharp", "runtime", "device.lock")
            : "/Library/Application Support/OssmSharp/runtime/device.lock";

        Assert.Equal(expected, DeviceOwnershipLock.LockFilePath);
    }

    [Fact]
    public async Task SecondLeaseCannotBeAcquiredUntilTheFirstLeaseIsReleasedFromAnotherThread()
    {
        var directory = CreateTestDirectory();
        try
        {
            var path = Path.Combine(directory, "device.lock");
            using var first = DeviceOwnershipLock.TryAcquireAtPath(path);

            Assert.NotNull(first);
            Assert.Null(DeviceOwnershipLock.TryAcquireAtPath(path));

            await Task.Run(first.Dispose, TestContext.Current.CancellationToken);
            using var replacement = DeviceOwnershipLock.TryAcquireAtPath(path);
            Assert.NotNull(replacement);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task FaultedCleanupKeepsLeaseAtFatalBoundary()
    {
        var directory = CreateTestDirectory();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanupFailure = new InvalidOperationException("simulated cleanup failure");
        var fatalBoundary = new InvalidOperationException("fatal boundary reached");
        DeviceOwnershipLock? ownership = null;
        try
        {
            var path = Path.Combine(directory, "device.lock");
            ownership = DeviceOwnershipLock.TryAcquireAtPath(path);
            Assert.NotNull(ownership);

            async ValueTask FaultedCleanupAsync()
            {
                await gate.Task.WaitAsync(TestContext.Current.CancellationToken);
                throw cleanupFailure;
            }

            var cleanup = ownership.ReleaseAfterAsync(FaultedCleanupAsync, () =>
            {
                using var contender = DeviceOwnershipLock.TryAcquireAtPath(path);
                Assert.Null(contender);
                throw fatalBoundary;
            }).AsTask();
            using var whilePending = DeviceOwnershipLock.TryAcquireAtPath(path);
            gate.SetResult();

            var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => cleanup);
            Assert.Same(fatalBoundary, thrown);
            Assert.Null(whilePending);

            using var afterFailure = DeviceOwnershipLock.TryAcquireAtPath(path);
            Assert.Null(afterFailure);
        }
        finally
        {
            gate.TrySetResult();
            ownership?.Dispose();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task SuccessfulCleanupReleasesLeaseAfterCleanupSettles()
    {
        var directory = CreateTestDirectory();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        DeviceOwnershipLock? ownership = null;
        try
        {
            var path = Path.Combine(directory, "device.lock");
            ownership = DeviceOwnershipLock.TryAcquireAtPath(path);
            Assert.NotNull(ownership);

            var cleanup = ownership.ReleaseAfterAsync(async () =>
                await gate.Task.WaitAsync(TestContext.Current.CancellationToken)).AsTask();
            using var whilePending = DeviceOwnershipLock.TryAcquireAtPath(path);
            gate.SetResult();

            await cleanup;
            Assert.Null(whilePending);
            using var replacement = DeviceOwnershipLock.TryAcquireAtPath(path);
            Assert.NotNull(replacement);
        }
        finally
        {
            gate.TrySetResult();
            ownership?.Dispose();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ReleasedLeaseLeavesTheLockFileInPlace()
    {
        var directory = CreateTestDirectory();
        try
        {
            var path = Path.Combine(directory, "device.lock");
            using (var first = DeviceOwnershipLock.TryAcquireAtPath(path))
                Assert.NotNull(first);

            Assert.True(File.Exists(path));
            using var replacement = DeviceOwnershipLock.TryAcquireAtPath(path);
            Assert.NotNull(replacement);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void MissingDirectoryFailsWithoutCreatingOne()
    {
        var directory = CreateTestDirectory();
        try
        {
            var missing = Path.Combine(directory, "unprovisioned");
            var exception = Assert.Throws<DirectoryNotFoundException>(() =>
                DeviceOwnershipLock.TryAcquireAtPath(Path.Combine(missing, "device.lock")));

            Assert.Contains("Provision", exception.Message);
            Assert.False(Directory.Exists(missing));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void AccessDeniedFailsClearlyInsteadOfReportingAnotherOwner()
    {
        var directory = CreateTestDirectory();
        var path = Path.Combine(directory, "device.lock");
        try
        {
            File.WriteAllText(path, string.Empty);
            File.SetAttributes(path, FileAttributes.ReadOnly);

            var exception = Assert.Throws<UnauthorizedAccessException>(() => DeviceOwnershipLock.TryAcquireAtPath(path));
            Assert.Contains("Grant", exception.Message);
        }
        finally
        {
            if (File.Exists(path))
                File.SetAttributes(path, FileAttributes.Normal);

            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateTestDirectory()
    {
        var root = OperatingSystem.IsWindows() ? "D:/temp" : Path.GetTempPath();
        var directory = Path.Combine(root, $"OssmSharp.Device.Runtime.Tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }
}
