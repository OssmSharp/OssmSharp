namespace OssmSharp.Device.Runtime;

/// <summary>
/// Holds the machine-wide device ownership lease until the owning session has finished cleanup.
/// </summary>
public sealed class DeviceOwnershipLock : IDisposable
{
    private readonly FileStream _file;

    private DeviceOwnershipLock(FileStream file) => _file = file;

    internal static string LockFilePath
    {
        get
        {
            if (OperatingSystem.IsWindows())
            {
                var root = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
                if (!Path.IsPathFullyQualified(root))
                    throw new DirectoryNotFoundException("Windows common application data is unavailable for device ownership.");

                return Path.Combine(root, "OssmSharp", "runtime", "device.lock");
            }

            if (OperatingSystem.IsMacOS())
                return "/Library/Application Support/OssmSharp/runtime/device.lock";

            throw new PlatformNotSupportedException("Device ownership requires Windows or macOS.");
        }
    }

    /// <summary>
    /// Acquires exclusive device ownership before connecting; only an existing owner returns null.
    /// </summary>
    /// <returns>The lease to retain through cleanup, or null when the device is already owned.</returns>
    /// <exception cref="DirectoryNotFoundException">The machine-wide runtime directory has not been provisioned.</exception>
    /// <exception cref="UnauthorizedAccessException">The owner cannot access the runtime lock file.</exception>
    public static DeviceOwnershipLock? TryAcquire() => TryAcquireAtPath(LockFilePath);

    /// <summary>Releases ownership after cleanup, or terminates the process if cleanup fails.</summary>
    /// <param name="cleanup">Completes device cleanup before releasing ownership.</param>
    public ValueTask ReleaseAfterAsync(Func<ValueTask> cleanup) =>
        ReleaseAfterAsync(cleanup, static () => Environment.FailFast("Device cleanup failed; ownership cannot be released safely."));

    /// <summary>Releases ownership without deleting the lock file.</summary>
    public void Dispose() => _file.Dispose();

    internal static DeviceOwnershipLock? TryAcquireAtPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        try
        {
            return new(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
        }
        catch (IOException exception) when (OperatingSystem.IsWindows()
            ? exception.HResult == unchecked((int)0x80070020)
            : OperatingSystem.IsMacOS() && exception.HResult == 35)
        {
            return null;
        }
        catch (DirectoryNotFoundException exception)
        {
            throw new DirectoryNotFoundException(
                $"Provision the machine-wide device runtime directory '{Path.GetDirectoryName(path)}' before starting a device owner.",
                exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new UnauthorizedAccessException(
                $"Device ownership requires access to '{path}'. Grant the intended local operator access to the runtime directory and lock file.",
                exception);
        }
    }

    internal async ValueTask ReleaseAfterAsync(Func<ValueTask> cleanup, Action terminateOwner)
    {
        ArgumentNullException.ThrowIfNull(cleanup);
        ArgumentNullException.ThrowIfNull(terminateOwner);

        try
        {
            await cleanup().ConfigureAwait(false);
        }
        catch
        {
            terminateOwner();
            throw;
        }

        Dispose();
    }
}
