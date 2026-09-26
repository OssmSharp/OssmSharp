#if WINDOWS
using System.Reactive.Linq;
using System.Threading.Channels;
using Shiny.BluetoothLE;

namespace OssmSharp.Device.Stock;

internal sealed class WindowsStockBleConnection : IStockBleConnection
{
    private readonly IPeripheral _peripheral;
    private readonly Guid _service;
    private readonly Channel<byte[]> _notifications = Channel.CreateBounded<byte[]>(
        new BoundedChannelOptions(32) { SingleReader = true, SingleWriter = false });
    private readonly IDisposable _disconnected;
    private readonly IDisposable _failed;
    private IDisposable? _subscription;

    internal WindowsStockBleConnection(IPeripheral peripheral, Guid service)
    {
        _peripheral = peripheral;
        _service = service;
        _disconnected = peripheral.WhenDisconnected().Subscribe(_ =>
            _notifications.Writer.TryComplete(new IOException("The stock BLE connection was lost.")));
        _failed = peripheral.WhenConnectionFailed().Subscribe(error =>
            _notifications.Writer.TryComplete(new IOException("The stock BLE connection failed.", error)));
    }

    public async Task<StockGattProfile> ReadProfileAsync(CancellationToken cancellationToken)
    {
        var services = await _peripheral.GetServicesAsync(cancellationToken).ConfigureAwait(false);
        if (!services.Any(info => Guid.TryParse(info.Uuid, out var uuid) && uuid == _service))
        {
            throw new InvalidDataException("The stock service is missing.");
        }

        var serviceId = _service.ToString();
        var characteristics = await _peripheral.GetCharacteristicsAsync(serviceId, cancellationToken)
            .ConfigureAwait(false);
        var characteristicIds = characteristics
            .Where(info => Guid.TryParse(info.Uuid, out _))
            .Select(info => Guid.Parse(info.Uuid))
            .ToArray();
        var identity = await _peripheral.ReadCharacteristicAsync(
            serviceId, StockFirmwareProfile.Identity.ToString(), cancellationToken).ConfigureAwait(false);
        var patterns = await _peripheral.ReadCharacteristicAsync(
            serviceId, StockFirmwareProfile.Patterns.ToString(), cancellationToken).ConfigureAwait(false);
        if (identity.Event != BleCharacteristicEvent.Read || patterns.Event != BleCharacteristicEvent.Read ||
            identity.Data is not { Length: > 0 and <= 256 } || patterns.Data is not { Length: > 0 and <= 4096 })
        {
            throw new InvalidDataException("The stock profile read returned invalid data.");
        }

        _subscription = _peripheral.NotifyCharacteristic(serviceId, StockFirmwareProfile.State.ToString())
            .Subscribe(result =>
            {
                if (result.Event == BleCharacteristicEvent.Notification &&
                    result.Data is { Length: > 0 and <= 4096 } data &&
                    _notifications.Writer.TryWrite(data.ToArray()))
                {
                    return;
                }

                _notifications.Writer.TryComplete(new InvalidDataException("The stock notification is invalid or the queue is full."));
            }, error => _notifications.Writer.TryComplete(new IOException("Stock notifications failed.", error)));
        await _peripheral.WaitForCharacteristicSubscriptionAsync(
            serviceId, StockFirmwareProfile.State.ToString(), cancellationToken).ConfigureAwait(false);

        return new StockGattProfile(
            _peripheral.Uuid, _service, characteristicIds, identity.Data, patterns.Data, _notifications.Reader);
    }

    public ValueTask DisposeAsync()
    {
        _subscription?.Dispose();
        _disconnected.Dispose();
        _failed.Dispose();
        _notifications.Writer.TryComplete();
        _peripheral.CancelConnection();
        return ValueTask.CompletedTask;
    }
}
#endif
