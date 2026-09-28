using System.Diagnostics.CodeAnalysis;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OssmSharp.Device.Primitives;
using OssmSharp.Device.Runtime;
using Shiny.BluetoothLE;

namespace OssmSharp.Device.Oem;

/// <summary>
/// Supplies OEM Bluetooth operations while Runtime owns settings execution, state, and arming.
/// </summary>
/// <remarks>
/// Native hosts must target Windows or macOS and provide platform Bluetooth permissions.
/// Use <see cref="ServiceCollectionExtensions.AddOemRuntime"/> when composing the native host so Shiny delivers teardown callbacks.
/// macOS reads temporarily detach notifications and await their detachment to avoid Shiny 5.7.2's shared read/notification callback.
/// A firmware acknowledgement is not a state observation and is surfaced as a read error, never cached success.
/// </remarks>
public sealed class OssmDevice : DeviceBase
{
    private static readonly ConditionalWeakTable<IPeripheral, OssmDevice> Owners = new();
    private readonly IBleManager manager;
    private readonly string identifier;
    private readonly CompositeDisposable subscriptions = new();
    private readonly CompositeDisposable lifecycle = new();
    private readonly TaskCompletionSource disconnected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SerialDisposable notifications = new();
    private IPeripheral? peripheral;
    private TaskCompletionSource<bool>? subscriptionChange;
    private Task? disconnect;
    private bool subscriptionRequested;
    private bool notificationsPaused;

    /// <summary>
    /// Initializes a caller-owned device selected by its discovered platform identifier.
    /// </summary>
    /// <param name="manager">The host-owned Bluetooth manager used for discovery.</param>
    /// <param name="identifier">The opaque identifier returned by <see cref="DeviceDiscovery"/>.</param>
    /// <param name="logger">The operational logger for Runtime's execution owner.</param>
    public OssmDevice(IBleManager manager, string identifier, ILogger<DeviceBase> logger)
        : base(logger)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier);
        this.manager = manager;
        this.identifier = identifier;
        subscriptions.Add(notifications);
    }

    internal static void PeripheralStateChanged(IPeripheral peripheral)
    {
        if (peripheral.Status == ConnectionState.Disconnected && Owners.TryGetValue(peripheral, out var owner))
        {
            owner.Disconnected();
        }
    }

    /// <inheritdoc />
    protected override async Task ConnectCoreAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var access = await manager.RequestAccessAsync().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (access != AccessState.Available)
        {
            throw new InvalidOperationException($"Bluetooth access is {access}. Enable Bluetooth and grant the application Bluetooth permission.");
        }

        var selected = manager.GetKnownPeripheral(identifier)
            ?? throw new InvalidOperationException("The selected Bluetooth device is unavailable. Discover devices again before connecting.");
        if (selected.Status != ConnectionState.Disconnected)
        {
            throw new InvalidOperationException("The selected Bluetooth device is already in use by another connection.");
        }

        if (!Owners.TryAdd(selected, this))
        {
            throw new InvalidOperationException("The selected Bluetooth device is already owned by another OEM connection.");
        }

        peripheral = selected;
        try
        {
            var connectIssued = false;
            lifecycle.Add(selected.WhenStatusChanged().Skip(1)
                .Where(status => status == ConnectionState.Disconnected)
                .Subscribe(_ => Disconnected(), NotificationFailed));
            lifecycle.Add(selected.WhenConnectionFailed().Subscribe(exception =>
            {
                if (Volatile.Read(ref connectIssued))
                {
                    disconnected.TrySetResult();
                    NotificationFailed(exception);
                }
            }, NotificationFailed));
            Volatile.Write(ref connectIssued, true);
            await selected.ConnectAsync(new ConnectionConfig(AutoConnect: false), cancellationToken).ConfigureAwait(false);
            subscriptions.Add(selected.WhenCharacteristicSubscriptionChanged(BleProtocol.Service, BleProtocol.State)
                .Subscribe(characteristic =>
                {
                    if (characteristic.IsNotifying)
                    {
                        Volatile.Read(ref subscriptionChange)?.TrySetResult(true);
                    }
                    else if (Volatile.Read(ref notificationsPaused))
                    {
                        Volatile.Read(ref subscriptionChange)?.TrySetResult(false);
                    }
                    else if (Volatile.Read(ref subscriptionRequested))
                    {
                        NotificationFailed(new IOException("The OEM Bluetooth state subscription could not be enabled or was lost."));
                    }
                }, NotificationFailed));

            await EnableNotificationsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception connectionError)
        {
            try
            {
                await DisconnectAsync().ConfigureAwait(false);
            }
            catch (Exception cleanupError)
            {
                throw new AggregateException("Bluetooth connection setup and cleanup both failed.", connectionError, cleanupError);
            }

            throw;
        }
    }

    /// <inheritdoc />
    [SuppressMessage("Usage", "CA1849", Justification = "Shiny's Async wrapper adds a timeout that abandons issued GATT I/O; the raw observable is asynchronous and awaited to settlement.")]
    protected override async Task WriteCommandAsync(DeviceCommand command, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await peripheral!
            .WriteCharacteristic(BleProtocol.Service, BleProtocol.Command, Request.Encode(command), withResponse: true)
            .ToTask()
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected override async Task<DeviceState> ReadStateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsMacOS())
        {
            return await ReadControlsAsync().ConfigureAwait(false);
        }

        try
        {
            var detached = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref subscriptionChange, detached);
            Volatile.Write(ref notificationsPaused, true);
            notifications.Disposable = Disposable.Empty;
            if (await detached.Task.ConfigureAwait(false))
            {
                throw new IOException("The OEM Bluetooth state notifications could not be detached before a live read.");
            }

            return await ReadControlsAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            NotificationFailed(exception);
            throw;
        }
    }

    /// <inheritdoc />
    protected override async Task CompleteStateReadAsync()
    {
        if (OperatingSystem.IsMacOS())
        {
            try
            {
                await EnableNotificationsAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                NotificationFailed(exception);
                throw;
            }
        }
    }

    /// <inheritdoc />
    protected override Task DisconnectAsync() => disconnect ??= DisconnectCoreAsync();

    private static DeviceState DecodeState(byte[]? data)
    {
        if (data is null || data.Length is 0 or > 512)
        {
            throw new InvalidDataException("The OEM state payload must contain between one and 512 bytes.");
        }

        try
        {
            using var document = JsonDocument.Parse(data, new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new InvalidDataException("The OEM state payload contains duplicate fields.");
                }
            }

            return new(new MotionSettings(
                root.GetProperty("pattern").GetInt32(),
                root.GetProperty("speed").GetInt32(),
                root.GetProperty("stroke").GetInt32(),
                root.GetProperty("depth").GetInt32(),
                root.GetProperty("sensation").GetInt32()));
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or ArgumentOutOfRangeException)
        {
            throw new InvalidDataException("The OEM state payload must be a JSON object containing integer pattern, speed, stroke, depth, and sensation arguments in their basic ranges.", exception);
        }
    }

    private async Task EnableNotificationsAsync(CancellationToken cancellationToken)
    {
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Write(ref subscriptionChange, ready);
        Volatile.Write(ref notificationsPaused, false);
        Volatile.Write(ref subscriptionRequested, true);
        var subscription = new SingleAssignmentDisposable();
        notifications.Disposable = subscription;
        subscription.Disposable = peripheral!.NotifyCharacteristic(BleProtocol.Service, BleProtocol.State, false)
            .Subscribe(result =>
            {
                try
                {
                    UpdateState(DecodeState(result.Data));
                }
                catch (InvalidDataException exception)
                {
                    NotificationFailed(exception);
                }
            }, NotificationFailed, () => NotificationFailed(new IOException("The OEM Bluetooth state subscription ended.")));
        await ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private void NotificationFailed(Exception exception)
    {
        Volatile.Read(ref subscriptionChange)?.TrySetException(exception);
        FailConnection(exception);
        notifications.Dispose();
    }

    private void Disconnected()
    {
        disconnected.TrySetResult();
        NotificationFailed(new IOException("The OEM Bluetooth connection was lost. Dispose the device and reconnect."));
    }

    [SuppressMessage("Usage", "CA1849", Justification = "Shiny's Async wrapper adds a timeout that abandons issued GATT I/O; the raw asynchronous read is awaited to settlement.")]
    private async Task<DeviceState> ReadControlsAsync()
    {
        var result = await peripheral!
            .ReadCharacteristic(BleProtocol.Service, BleProtocol.State)
            .ToTask()
            .ConfigureAwait(false);
        return DecodeState(result.Data);
    }

    private async Task DisconnectCoreAsync()
    {
        Volatile.Write(ref subscriptionRequested, false);
        subscriptions.Dispose();
        if (peripheral is not null)
        {
            peripheral.CancelConnection();
            await disconnected.Task.ConfigureAwait(false);
            lifecycle.Dispose();
            Owners.Remove(peripheral);
        }
    }
}
