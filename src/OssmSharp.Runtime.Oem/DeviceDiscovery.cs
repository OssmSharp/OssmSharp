using System.Reactive.Linq;
using Shiny.BluetoothLE;

namespace OssmSharp.Runtime.Oem;

/// <summary>
/// Discovers devices advertising the fixed OSSM bluetooth service without connecting to them.
/// </summary>
public sealed class DeviceDiscovery
{
    private readonly IBleManager manager;
    private readonly TimeProvider timeProvider;
    private int scanning;

    /// <summary>
    /// Initializes discovery using the application's bluetooth manager and clock.
    /// </summary>
    /// <param name="manager">The host-owned Shiny bluetooth manager.</param>
    /// <param name="timeProvider">The clock used to bound each scan.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="manager"/> or <paramref name="timeProvider"/> is <see langword="null"/>.
    /// </exception>
    public DeviceDiscovery(IBleManager manager, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(timeProvider);
        this.manager = manager;
        this.timeProvider = timeProvider;
    }

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
    public Task<IReadOnlyList<DiscoveredDevice>> ScanAsync(CancellationToken cancellationToken = default)
        => ScanAsync(false, cancellationToken);

    /// <summary>
    /// Scans until the first matching device is discovered or five seconds elapse.
    /// </summary>
    /// <param name="cancellationToken"/>
    /// <returns>The first matching device, or <see langword="null"/> when none is discovered.</returns>
    /// <inheritdoc cref="ScanAsync(CancellationToken)"/>
    public async Task<DiscoveredDevice?> FindFirstAsync(CancellationToken cancellationToken = default)
    {
        var devices = await ScanAsync(true, cancellationToken).ConfigureAwait(false);
        return devices.Count == 0 ? null : devices[0];
    }

    /// <summary>
    /// Scans for matching devices for up to five seconds, optionally stopping at the first matching advertisement.
    /// </summary>
    /// <param name="stopAfterFirst">Whether to stop scanning as soon as one matching device is discovered.</param>
    /// <param name="cancellationToken"/>
    /// <inheritdoc cref="ScanAsync(CancellationToken)"/>
    public async Task<IReadOnlyList<DiscoveredDevice>> ScanAsync(bool stopAfterFirst, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref scanning, 1, 0) != 0)
        {
            throw new InvalidOperationException("A device discovery scan is already running.");
        }

        try
        {
            var access = await manager.RequestAccessAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (access != AccessState.Available)
            {
                throw new InvalidOperationException($"Bluetooth access is {access}. Enable bluetooth and grant the application bluetooth permission.");
            }

            if (manager.IsScanning)
            {
                throw new InvalidOperationException("The bluetooth manager is already scanning.");
            }

            var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var completion = new TaskCompletionSource<IList<DiscoveredDevice>>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var scan = manager
                .Scan(new ScanConfig(BleProtocol.Service))
                .Finally(() => stopped.TrySetResult())
                .TakeUntil(Observable.FromAsync(token => Task.Delay(TimeSpan.FromSeconds(5), timeProvider, token)))
                .Distinct(result => result.Peripheral.Uuid, StringComparer.OrdinalIgnoreCase)
                .Take(stopAfterFirst ? 1 : int.MaxValue)
                .Select(result => new DiscoveredDevice(result.Peripheral.Uuid, result.Peripheral.Name))
                .ToList()
                .Subscribe(results => completion.TrySetResult(results), exception => completion.TrySetException(exception));

            try
            {
                return (await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false)).ToArray();
            }
            finally
            {
                scan.Dispose();
                await stopped.Task.ConfigureAwait(false);
            }
        }
        finally
        {
            Interlocked.Exchange(ref scanning, 0);
        }
    }
}
