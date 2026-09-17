namespace OSSMDotNet.ControlService;

/// <summary>Owns the process-wide control-device lease for the lifetime of one control service.</summary>
internal sealed class DeviceOwnershipLock : IDisposable
{
    private const string LocalName = "ossm-web-controls-prototype-device";
    private readonly Mutex _mutex;
    private int _disposed;

    private DeviceOwnershipLock(Mutex mutex) => _mutex = mutex;

    /// <summary>Gets the OS-wide mutex name used for the current platform.</summary>
    internal static string Name => OperatingSystem.IsWindows() ? $@"Global\{LocalName}" : LocalName;

    /// <summary>Tries to acquire the single-writer lease without treating an existing owner as an exception.</summary>
    internal static DeviceOwnershipLock? TryAcquire()
    {
        try
        {
            var mutex = new Mutex(initiallyOwned: true, Name, out var createdNew);
            if (!createdNew)
            {
                try
                {
                    if (!mutex.WaitOne(TimeSpan.Zero))
                    {
                        mutex.Dispose();
                        return null;
                    }
                }
                catch (AbandonedMutexException)
                {
                    // The abandoned mutex is acquired, so a stale owner cannot block safe startup.
                }
            }

            return new(mutex);
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Releases the lease before closing its OS handle.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        try { _mutex.ReleaseMutex(); }
        finally { _mutex.Dispose(); }
        GC.SuppressFinalize(this);
    }
}
