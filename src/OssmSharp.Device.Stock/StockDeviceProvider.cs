#if WINDOWS || MACOS
using OssmSharp.Device.Stock.Models;
using Shiny;
using Shiny.BluetoothLE;

namespace OssmSharp.Device.Stock;

/// <summary>Reads the stock pairing identity without opening a motion session.</summary>
public sealed class StockDeviceProvider(
    Lazy<IBleManager> nativeManager,
    TimeProvider? timeProvider = null) : IDeviceProvider
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    /// <inheritdoc />
    public async Task<DeviceIdentityDetails> ReadInformationAsync(
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
                return new DeviceIdentityDetails(
                    device,
                    identity.WifiMac,
                    identity.Chip,
                    identity.WifiConnected,
                    identity.SketchMd5,
                    identity.Version);
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
}
#endif
