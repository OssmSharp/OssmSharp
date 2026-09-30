namespace OssmSharp.Runtime.Oem;

/// <summary>
/// Discovers devices advertising the fixed OSSM bluetooth service without connecting to them.
/// </summary>
public interface IDeviceDiscovery
{
    /// <summary>
    /// Requests bluetooth access and scans for matching devices for up to five seconds without connecting to them.
    /// </summary>
    /// <param name="cancellationToken"/>
    /// <returns>
    /// The first identifier and name observed for each distinct platform identifier, compared without regard to case;
    /// an empty collection when no matching devices are discovered.
    /// </returns>
    /// <remarks>
    /// The five-second limit excludes the permission request, which cancellation does not interrupt.
    /// Cancellation is checked before and after that request and while collecting scan results.
    /// An acquired scan subscription is disposed before completion, including on cancellation or failure;
    /// the host-owned bluetooth manager is not disposed. Cancellation and bluetooth errors propagate without returning partial results.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Another scan is active on this instance or the bluetooth manager, or bluetooth access is unavailable.
    /// </exception>
    /// <exception cref="OperationCanceledException">Discovery was cancelled.</exception>
    Task<IReadOnlyList<DiscoveredDevice>> ScanAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Scans until the first matching device is discovered or five seconds elapse.
    /// </summary>
    /// <param name="cancellationToken"/>
    /// <returns>The first matching device, or <see langword="null"/> when none is discovered.</returns>
    /// <inheritdoc cref="ScanAsync(CancellationToken)"/>
    Task<DiscoveredDevice?> FindFirstAsync(CancellationToken cancellationToken = default);
}
