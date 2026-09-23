namespace OSSMDotNet.Api.Device;

/// <summary>
/// Owns the OS-wide control-device lease for the lifetime of one API host.
/// </summary>
internal sealed class DeviceOwnershipLock : IDisposable
{
    private const string LocalName = "ossm-web-controls-prototype-device";
    private readonly Semaphore _semaphore;
    private int _disposed;

    private DeviceOwnershipLock(Semaphore semaphore) => _semaphore = semaphore;

    /// <summary>
    /// Gets the OS-wide semaphore name used for the current platform.
    /// </summary>
    internal static string Name => OperatingSystem.IsWindows() ? $@"Global\{LocalName}" : LocalName;

    /// <summary>
    /// Tries to acquire the single-writer lease without treating an existing owner as an exception.
    /// </summary>
    /// <returns>The owned lease, or null when another host owns it or access is denied.</returns>
    internal static DeviceOwnershipLock? TryAcquire()
    {
        try
        {
            var semaphore = new Semaphore(initialCount: 1, maximumCount: 1, Name, out _);
            if (!semaphore.WaitOne(TimeSpan.Zero))
            {
                semaphore.Dispose();
                return null;
            }

            return new(semaphore);
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Releases the non-thread-affine lease before closing its OS handle.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        try { _semaphore.Release(); }
        finally { _semaphore.Dispose(); }
        GC.SuppressFinalize(this);
    }
}
