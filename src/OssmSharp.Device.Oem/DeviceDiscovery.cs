using System.Reactive.Linq;
using Shiny.BluetoothLE;

namespace OssmSharp.Device.Oem;

/// <summary>
/// Discovers OEM devices advertising the fixed OSSM Bluetooth service without connecting to them.
/// </summary>
public sealed class DeviceDiscovery
{
    private readonly IBleManager manager;
    private readonly TimeProvider timeProvider;
    private int scanning;

    /// <summary>
    /// Initializes discovery using the application's Bluetooth manager and clock.
    /// </summary>
    /// <param name="manager">The host-owned Shiny Bluetooth manager.</param>
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
    /// Requests Bluetooth access and scans for OEM devices for up to five seconds without connecting to them.
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
    /// the host-owned Bluetooth manager is not disposed. Cancellation and Bluetooth errors propagate without returning partial results.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Another scan is active on this instance or the Bluetooth manager, or Bluetooth access is unavailable.
    /// </exception>
    /// <exception cref="OperationCanceledException">Discovery was cancelled.</exception>
    public async Task<IReadOnlyList<DiscoveredDevice>> ScanAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref scanning, 1, 0) != 0)
        {
            throw new InvalidOperationException("An OEM discovery scan is already running.");
        }

        try
        {
            var access = await manager.RequestAccessAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (access != AccessState.Available)
            {
                throw new InvalidOperationException($"Bluetooth access is {access}. Enable Bluetooth and grant the application Bluetooth permission.");
            }

            if (manager.IsScanning)
            {
                throw new InvalidOperationException("The Bluetooth manager is already scanning.");
            }

            var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var completion = new TaskCompletionSource<IList<DiscoveredDevice>>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var scan = manager
                .Scan(new ScanConfig(BleProtocol.Service))
                .Finally(() => stopped.TrySetResult())
                .TakeUntil(Observable.FromAsync(token => Task.Delay(TimeSpan.FromSeconds(5), timeProvider, token)))
                .Distinct(result => result.Peripheral.Uuid, StringComparer.OrdinalIgnoreCase)
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
