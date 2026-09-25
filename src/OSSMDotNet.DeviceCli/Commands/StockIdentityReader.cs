#if MACOS
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using CoreFoundation;
using Microsoft.Extensions.DependencyInjection;
using OSSMDotNet.Models;
using Shiny;
using Shiny.BluetoothLE;

namespace OSSMDotNet.DeviceCli.Commands;

/// <summary>
/// Reads the live stock identity of one selected macOS peripheral.
/// </summary>
internal static class StockIdentityReader
{
    internal static async Task<string> ReadAsync(
        string? peripheralId,
        TextReader input,
        TextWriter prompt,
        bool isInteractive,
        CancellationToken cancellationToken)
    {
        if (peripheralId is null && !isInteractive)
        {
            throw new InvalidOperationException("Interactive selection requires a terminal. Supply --peripheral-id instead.");
        }

        using var dispatchQueue = new DispatchQueue("org.ossm.devicecli.identify");
        using var services = new ServiceCollection()
            .AddLogging()
            .AddBluetoothLE(new AppleBleConfiguration(DispatchQueue: dispatchQueue))
            .BuildServiceProvider();
        var manager = services.GetRequiredService<IBleManager>();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));

        try
        {
            var access = await manager.RequestAccess().Take(1).ToTask(timeout.Token)
                .ConfigureAwait(false);
            if (access != AccessState.Available)
            {
                throw new InvalidOperationException($"Bluetooth access is {access}.");
            }

            var service = StockFirmwareProfile.Service.ToString();
            var selected = await SelectPeripheralAsync(
                manager, service, peripheralId, input, prompt, isInteractive, timeout, cancellationToken)
                .ConfigureAwait(false);

            try
            {
                await selected.ConnectAsync(new ConnectionConfig(AutoConnect: false), timeout.Token)
                    .ConfigureAwait(false);
                var result = await selected.ReadCharacteristicAsync(
                    service,
                    StockFirmwareProfile.Identity.ToString(),
                    timeout.Token).ConfigureAwait(false);
                if (result.Event != BleCharacteristicEvent.Read || result.Data is not { Length: > 0 and <= 256 })
                {
                    throw new InvalidDataException("The stock identity read returned an invalid payload.");
                }

                var identity = PairingIdentity.Parse(result.Data);
                return $"Wi-Fi MAC: {identity.WifiMac}{Environment.NewLine}" +
                    $"Chip: {identity.Chip}{Environment.NewLine}" +
                    $"Wi-Fi connected: {identity.WifiConnected}{Environment.NewLine}" +
                    $"Sketch MD5: {identity.SketchMd5}{Environment.NewLine}" +
                    $"Firmware: {identity.Version}";
            }
            finally
            {
                selected.CancelConnection();
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Read-only identification timed out.");
        }
    }

    private static async Task<IPeripheral> SelectPeripheralAsync(
        IBleManager manager,
        string service,
        string? peripheralId,
        TextReader input,
        TextWriter prompt,
        bool isInteractive,
        CancellationTokenSource timeout,
        CancellationToken cancellationToken)
    {
        if (peripheralId is not null)
        {
            return await manager.Scan(new ScanConfig(service))
                .Where(result => string.Equals(result.Peripheral.Uuid, peripheralId, StringComparison.OrdinalIgnoreCase))
                .Select(result => result.Peripheral)
                .Take(1)
                .ToTask(timeout.Token).ConfigureAwait(false);
        }

        var candidates = await manager.Scan(new ScanConfig(service))
            .Where(result => result.AdvertisementData.ServiceUuids?.Any(uuid =>
                string.Equals(uuid, service, StringComparison.OrdinalIgnoreCase)) == true)
            .Distinct(result => result.Peripheral.Uuid)
            .Take(64)
            .Buffer(TimeSpan.FromSeconds(5))
            .Take(1)
            .ToTask(timeout.Token).ConfigureAwait(false);

        timeout.CancelAfter(Timeout.InfiniteTimeSpan);
        var selectedId = await DeviceSelection.SelectAsync(
            candidates.Select(result => (
                result.AdvertisementData.LocalName ?? result.Peripheral.Name ?? "Unnamed device",
                result.Peripheral.Uuid)).ToList(),
            input,
            prompt,
            isInteractive,
            cancellationToken).ConfigureAwait(false);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        return candidates.First(result => result.Peripheral.Uuid == selectedId).Peripheral;
    }
}
#endif
