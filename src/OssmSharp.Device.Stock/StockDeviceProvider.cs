#if WINDOWS || MACOS
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using OssmSharp.Device.Stock.Models;
using Shiny;
using Shiny.BluetoothLE;

namespace OssmSharp.Device.Stock;

/// <summary>Discovers stock peripherals and reads pairing identity without opening a motion session.</summary>
public sealed class StockDeviceProvider(
    Lazy<IBleManager> nativeManager,
    TimeProvider timeProvider,
    IScheduler scheduler) : IDeviceProvider
{
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    private readonly IScheduler _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));

    /// <inheritdoc />
    public async Task<IReadOnlyList<DeviceIdentifier>> GetAvailableDevicesAsync(CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(nativeManager);
        cancellationToken.ThrowIfCancellationRequested();
        var manager = nativeManager.Value;
        var access = await manager
            .RequestAccessAsync()
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        if (access != AccessState.Available)
            throw new UnauthorizedAccessException("Bluetooth access is unavailable.");

        var service = StockFirmwareProfile.Service;
        var candidates = await manager
            .Scan(new ScanConfig(service.ToString()))
            .Where(result => HasService(result.AdvertisementData.ServiceUuids, service))
            .Where(result => !string.IsNullOrWhiteSpace(result.Peripheral.Uuid))
            .Distinct(result => result.Peripheral.Uuid, StringComparer.OrdinalIgnoreCase)
            .Take(64)
            .Buffer(TimeSpan.FromSeconds(5), _scheduler)
            .Take(1)
            .ToTask(cancellationToken).ConfigureAwait(false);

        return candidates
            .Select(result => (DeviceIdentifier)new StockDeviceIdentifier(
                result.Peripheral.Uuid,
                StockBleDeviceName.Select(result.AdvertisementData.LocalName, result.Peripheral.Name),
                result.Peripheral,
                manager))
            .ToArray();
    }

    /// <inheritdoc />
    public async Task<DeviceInformation> GetDeviceAsync(
        DeviceIdentifier device,
        CancellationToken cancellationToken)
    {
        if (device is not StockDeviceIdentifier)
        {
            throw new ArgumentException("The selected device does not belong to this stock BLE composition.", nameof(device));
        }

        ArgumentNullException.ThrowIfNull(nativeManager);
        cancellationToken.ThrowIfCancellationRequested();
        var manager = nativeManager.Value;
        StockDeviceIdentifier.Validate(device, manager);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30), _timeProvider);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);

        try
        {
            var access = await manager
                .RequestAccessAsync()
                .WaitAsync(timeout.Token)
                .ConfigureAwait(false);
            if (access != AccessState.Available)
            {
                throw new InvalidOperationException($"Bluetooth access is {access}.");
            }

            var peripheral = manager.GetKnownPeripheral(device.Id)
                ?? throw new InvalidOperationException("The discovered peripheral is no longer available.");
            if (!string.Equals(peripheral.Uuid, device.Id, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The resolved peripheral differs from the selected platform ID.");
            }

            try
            {
                await peripheral
                    .ConnectAsync(new ConnectionConfig(AutoConnect: false), timeout.Token)
                    .ConfigureAwait(false);
                var result = await peripheral
                    .ReadCharacteristicAsync(
                        StockFirmwareProfile.Service.ToString(),
                        StockFirmwareProfile.Identity.ToString(),
                        timeout.Token)
                    .ConfigureAwait(false);
                if (result.Event != BleCharacteristicEvent.Read || result.Data is not { Length: > 0 and <= 256 })
                {
                    throw new InvalidDataException("The stock identity read returned an invalid payload.");
                }

                var identity = PairingIdentity.Parse(result.Data);
                return new DeviceInformation(device.Id, null, identity.Version, [])
                {
                    Identifier = device,
                    WifiMac = identity.WifiMac,
                    Chip = identity.Chip,
                    WifiConnected = identity.WifiConnected,
                    SketchMd5 = identity.SketchMd5,
                };
            }
            finally
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5), _timeProvider);
                try
                {
                    await peripheral
                        .DisconnectAsync(cleanup.Token, TimeSpan.FromSeconds(5))
                        .WaitAsync(cleanup.Token)
                        .ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    throw new UnsettledDeviceCleanupException(
                        "Stock BLE disconnection did not settle.", exception);
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Read-only identity retrieval timed out.");
        }
    }

    private static bool HasService(IEnumerable<string>? advertisedServices, Guid service) =>
        advertisedServices?.Any(uuid => Guid.TryParse(uuid, out var advertised) && advertised == service) == true;
}
#endif
