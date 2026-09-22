namespace OSSMDotNet.DeviceCli;

/// <summary>
/// Owns the exclusive process lifetime of a local simulator host.
/// </summary>
internal static class SimulatorHost
{
    /// <summary>
    /// Runs a host while holding its exclusive ownership lock.
    /// </summary>
    /// <param name="simulatorPath">The native simulator executable to own.</param>
    /// <param name="endpoint">The host endpoint and ownership lock.</param>
    /// <param name="cancellationToken">The token that stops the host.</param>
    /// <returns>A task that completes with the host exit code.</returns>
    internal static async Task<int> RunAsync(
        string simulatorPath,
        SimulatorEndpoint endpoint,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(simulatorPath);
        ArgumentNullException.ThrowIfNull(endpoint);

        await using var ownership = TryAcquire(endpoint.LockFilePath);
        if (ownership is null)
        {
            return 1;
        }

        await using var session = new SimulatorHostSession(simulatorPath, endpoint);
        return await session.RunAsync(cancellationToken).ConfigureAwait(false);
    }

    private static FileStream? TryAcquire(string lockFilePath)
    {
        try
        {
            return new FileStream(
                lockFilePath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.Asynchronous | FileOptions.DeleteOnClose);
        }
        catch (IOException)
        {
            return null;
        }
    }
}
