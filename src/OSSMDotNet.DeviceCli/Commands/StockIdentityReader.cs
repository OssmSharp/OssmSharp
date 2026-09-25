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
/// Reads the live stock identity of one explicitly selected macOS peripheral.
/// </summary>
internal static class StockIdentityReader
{
    internal static async Task<string> ReadAsync(string peripheralId, CancellationToken cancellationToken)
    {
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
            var selected = await manager.Scan(new ScanConfig(service))
                .Where(result => string.Equals(result.Peripheral.Uuid, peripheralId, StringComparison.OrdinalIgnoreCase))
                .Select(result => result.Peripheral)
                .Take(1)
                .ToTask(timeout.Token).ConfigureAwait(false);

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
}
#endif
