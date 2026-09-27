#if WINDOWS || MACOS
using OssmSharp.Device.Stock;
using OssmSharp.Device.Stock.Models;
using Shiny;
using Shiny.BluetoothLE;
#endif

namespace OssmSharp.Cli.Commands;

internal sealed class StockIdentityReader
{
    private readonly Func<CancellationToken, Task<IReadOnlyList<(string Name, string Id)>>> _scan;
    private readonly Func<string, CancellationToken, Task<string>> _read;

#if WINDOWS || MACOS
    internal StockIdentityReader(IBleManager manager)
        : this(
            cancellationToken => StockBleDiscovery.ScanAsync(manager, cancellationToken),
            (id, cancellationToken) => ReadIdentityAsync(manager, id, cancellationToken))
    {
    }
#endif

    internal StockIdentityReader(
        Func<CancellationToken, Task<IReadOnlyList<(string Name, string Id)>>> scan,
        Func<string, CancellationToken, Task<string>> read)
    {
        _scan = scan ?? throw new ArgumentNullException(nameof(scan));
        _read = read ?? throw new ArgumentNullException(nameof(read));
    }

    internal async Task<string> ReadAsync(string id, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        var candidates = await _scan(cancellationToken).ConfigureAwait(false);
        var selected = candidates.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, id, StringComparison.OrdinalIgnoreCase));
        if (selected.Id is null)
        {
            throw new InvalidOperationException(
                $"No stock device with platform ID '{DeviceText.Sanitize(id)}' was discovered.");
        }

        var details = await _read(selected.Id, cancellationToken).ConfigureAwait(false);
        var name = string.IsNullOrWhiteSpace(selected.Name)
            ? string.Empty
            : $"Name: {DeviceText.Sanitize(selected.Name)}{Environment.NewLine}";
        return $"Peripheral ID: {DeviceText.Sanitize(selected.Id)}{Environment.NewLine}{name}{details}";
    }

#if WINDOWS || MACOS
    private static async Task<string> ReadIdentityAsync(
        IBleManager manager,
        string id,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));

        try
        {
            var access = await manager.RequestAccessAsync()
                .WaitAsync(timeout.Token).ConfigureAwait(false);
            if (access != AccessState.Available)
            {
                throw new InvalidOperationException($"Bluetooth access is {access}.");
            }

            var peripheral = manager.GetKnownPeripheral(id)
                ?? throw new InvalidOperationException("The discovered peripheral is no longer available.");
            if (!string.Equals(peripheral.Uuid, id, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The resolved peripheral differs from the selected platform ID.");
            }

            try
            {
                await peripheral.ConnectAsync(new ConnectionConfig(AutoConnect: false), timeout.Token)
                    .ConfigureAwait(false);
                var result = await peripheral.ReadCharacteristicAsync(
                    StockFirmwareProfile.Service.ToString(),
                    StockFirmwareProfile.Identity.ToString(),
                    timeout.Token).ConfigureAwait(false);
                if (result.Event != BleCharacteristicEvent.Read || result.Data is not { Length: > 0 and <= 256 })
                {
                    throw new InvalidDataException("The stock identity read returned an invalid payload.");
                }

                var identity = PairingIdentity.Parse(result.Data);
                return $"Wi-Fi MAC: {DeviceText.Sanitize(identity.WifiMac)}{Environment.NewLine}"
                    + $"Chip: {DeviceText.Sanitize(identity.Chip)}{Environment.NewLine}"
                    + $"Wi-Fi connected: {identity.WifiConnected}{Environment.NewLine}"
                    + $"Sketch MD5: {DeviceText.Sanitize(identity.SketchMd5)}{Environment.NewLine}"
                    + $"Firmware: {DeviceText.Sanitize(identity.Version)}";
            }
            finally
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try
                {
                    await peripheral.DisconnectAsync(cleanup.Token, TimeSpan.FromSeconds(5))
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
#endif
}
