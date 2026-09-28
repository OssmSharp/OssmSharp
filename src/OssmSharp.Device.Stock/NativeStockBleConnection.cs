#if WINDOWS || MACOS
using System.Reactive.Linq;
using System.Text;
using System.Threading.Channels;
using OssmSharp.Device.Stock.Models;
using Shiny.BluetoothLE;

namespace OssmSharp.Device.Stock;

internal sealed class NativeStockBleConnection : IDeviceConnection
{
    private static readonly TimeSpan DisconnectTimeout = TimeSpan.FromSeconds(5);
    private readonly IPeripheral _peripheral;
    private readonly Guid _service;
    private readonly TimeProvider _timeProvider;
    private readonly Channel<byte[]> _notifications = Channel.CreateBounded<byte[]>(
        new BoundedChannelOptions(32) { SingleReader = true, SingleWriter = false });
    private readonly IDisposable _disconnected;
    private readonly IDisposable _failed;
    private IDisposable? _subscription;
    private bool _initialised;

    internal NativeStockBleConnection(IPeripheral peripheral, Guid service, TimeProvider? timeProvider = null)
    {
        _peripheral = peripheral;
        _service = service;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _disconnected = peripheral.WhenDisconnected().Subscribe(OnDisconnected);
        _failed = peripheral.WhenConnectionFailed().Subscribe(OnConnectionFailed);
        _initialised = true;
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

    public async Task WriteCommandAsync(Request command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        cancellationToken.ThrowIfCancellationRequested();
        var payload = Encoding.UTF8.GetBytes(command.ToPayload());
        await _peripheral.WriteCharacteristicAsync(
                _service.ToString(), StockFirmwareProfile.Command.ToString(), payload,
                withResponse: true, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task EnableKnobLimitingAsync(
        CancellationToken requestCancellationToken,
        CancellationToken shutdownToken)
    {
        requestCancellationToken.ThrowIfCancellationRequested();
        var serviceId = _service.ToString();
        var configurationId = StockFirmwareProfile.KnobConfiguration.ToString();
        await _peripheral.WriteCharacteristicAsync(
                serviceId, configurationId, "true"u8.ToArray(),
                withResponse: true, shutdownToken)
            .ConfigureAwait(false);
        requestCancellationToken.ThrowIfCancellationRequested();
        var readback = await _peripheral.ReadCharacteristicAsync(
                serviceId, configurationId, shutdownToken)
            .ConfigureAwait(false);
        if (readback.Event != BleCharacteristicEvent.Read
            || readback.Data is null
            || !readback.Data.AsSpan().SequenceEqual("true"u8))
            throw new InvalidDataException(
                "The stock device did not confirm knob limiting through a live configuration read.");
        requestCancellationToken.ThrowIfCancellationRequested();
    }

    public async ValueTask DisposeAsync()
    {
        _subscription?.Dispose();
        _disconnected.Dispose();
        _failed.Dispose();
        _notifications.Writer.TryComplete();
        if (_peripheral.Status == ConnectionState.Disconnected)
        {
            _peripheral.CancelConnection();
            return;
        }

        await StockBleDisconnection.DisconnectAsync(
            token => _peripheral.DisconnectAsync(token, DisconnectTimeout),
            _timeProvider,
            DisconnectTimeout).ConfigureAwait(false);
    }

    private void OnDisconnected(IPeripheral _)
    {
        if (_initialised)
            _notifications.Writer.TryComplete(new IOException("The stock BLE connection was lost."));
    }

    private void OnConnectionFailed(BleException error)
    {
        if (_initialised)
            _notifications.Writer.TryComplete(new IOException("The stock BLE connection failed.", error));
    }
}
#endif
