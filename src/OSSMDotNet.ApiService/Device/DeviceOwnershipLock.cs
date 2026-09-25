namespace OSSMDotNet.ApiService.Device;

/// <summary>
/// Owns the cross-process control-device lease for the lifetime of one API host.
/// </summary>
internal sealed class DeviceOwnershipLock : IDisposable
{
    private const string LocalName = "ossm-web-controls-device";
    private readonly Semaphore? _semaphore;
    private readonly FileStream? _file;
    private int _disposed;

    private DeviceOwnershipLock(Semaphore semaphore) => _semaphore = semaphore;
    private DeviceOwnershipLock(FileStream file) => _file = file;

    /// <summary>
    /// Gets the semaphore name on Windows and the lock-file stem on Unix.
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
            if (!OperatingSystem.IsWindows())
            {
                var path = Path.Combine(Path.GetTempPath(), $"{LocalName}.lock");
                try
                {
                    return new(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
                }
                catch (IOException)
                {
                    return null;
                }
            }

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

        if (_semaphore is not null)
        {
            try { _semaphore.Release(); }
            finally { _semaphore.Dispose(); }
        }
        else
        {
            _file?.Dispose();
        }
        GC.SuppressFinalize(this);
    }
}
