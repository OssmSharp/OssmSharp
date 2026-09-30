using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Shiny.BluetoothLE;

namespace OssmSharp.Runtime.Oem;

/// <summary>
/// Supplies RAD BLE v1 operations while Runtime owns settings execution, state, and arming.
/// </summary>
/// <remarks>
/// Native hosts must target Windows or macOS and provide platform bluetooth permissions.
/// Register the native host through the service collection extension so Shiny delivers teardown callbacks.
/// Each connection validates the protocol, subscribes to responses and the sensor stream, acquires a control lease,
/// and streams motion observations. Lease tokens, request identifiers, and subscriptions stay internal.
/// A terminal response confirms firmware completion, not physical motion. Lease loss fails the connection;
/// the lease is never silently reacquired. The firmware can emit malformed JSON; such responses and motion
/// payloads are discarded, and a request without a terminal response is resent with its original identifier
/// a bounded number of times before the connection fails.
/// </remarks>
public sealed class OssmDevice : DeviceBase
{
    private const int MaximumMessageBytes = 509;
    private const int StreamHeaderBytes = 20;
    private const int MaximumRequestAttempts = 3;
    private static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(2);
    private static readonly ConditionalWeakTable<IPeripheral, OssmDevice> Owners = [];
    private readonly IBleManager manager;
    private readonly string identifier;
    private readonly TimeProvider timeProvider;
    private readonly ILogger logger;
    private readonly Lock sync = new();
    private readonly SemaphoreSlim writes = new(1, 1);
    private readonly Dictionary<uint, TaskCompletionSource<Response>> requests = [];
    private TaskCompletionSource? stopTransfer;
    private CompositeDisposable subscriptions = [];
    private CompositeDisposable lifecycle = [];
    private TaskCompletionSource disconnected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource<DeviceState>? liveRead;
    private CancellationTokenSource? leaseStopping;
    private Task leaseMaintenance = Task.CompletedTask;
    private IPeripheral? peripheral;
    private long generation;
    private bool active;
    private uint nextRequestId;
    private uint lease;
    private byte streamId;

    /// <summary>
    /// Initializes a caller-owned, reusable client for a device selected by its discovered platform identifier.
    /// </summary>
    /// <param name="manager">The host-owned bluetooth manager used for discovery.</param>
    /// <param name="identifier">The opaque identifier returned by <see cref="DeviceDiscovery"/>.</param>
    /// <param name="timeProvider">The clock used to renew the control lease before its reported expiry and to detect lost responses.</param>
    /// <param name="logger">The operational logger for Runtime's execution owner.</param>
    public OssmDevice(IBleManager manager, string identifier, TimeProvider timeProvider, ILogger<DeviceBase> logger)
        : base(logger)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier);
        ArgumentNullException.ThrowIfNull(timeProvider);
        this.manager = manager;
        this.identifier = identifier;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    internal static void PeripheralStateChanged(IPeripheral peripheral)
    {
        if (peripheral.Status == ConnectionState.Disconnected && Owners.TryGetValue(peripheral, out var owner))
        {
            owner.ConnectionLost(new IOException("The bluetooth connection was lost."));
        }
    }

    /// <inheritdoc />
    [SuppressMessage("Usage", "CA1849", Justification = "Shiny's Async wrappers add a timeout that abandons issued GATT I/O; the raw observables are awaited to settlement.")]
    protected override async Task ConnectCoreAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var access = await manager.RequestAccessAsync().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (access != AccessState.Available)
        {
            throw new InvalidOperationException($"Bluetooth access is {access}. Enable bluetooth and grant the application bluetooth permission.");
        }

        var selected = manager.GetKnownPeripheral(identifier)
            ?? throw new InvalidOperationException("The selected bluetooth device is unavailable. Discover devices again before connecting.");
        if (selected.Status != ConnectionState.Disconnected)
        {
            throw new InvalidOperationException("The selected bluetooth device is already in use by another connection.");
        }

        if (!Owners.TryAdd(selected, this))
        {
            throw new InvalidOperationException("The selected bluetooth device is already owned by another connection.");
        }

        long current;
        CompositeDisposable owned;
        CompositeDisposable events;
        var stopping = new CancellationTokenSource();
        var acquired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (sync)
        {
            current = ++generation;
            active = true;
            peripheral = selected;
            disconnected = new(TaskCreationOptions.RunContinuationsAsynchronously);
            subscriptions = owned = [];
            lifecycle = events = [];
            leaseStopping = stopping;
            nextRequestId = 0;
            lease = 0;
            streamId = 0;
        }

        var connectIssued = false;
        events.Add(selected.WhenStatusChanged().Skip(1)
            .Where(status => status == ConnectionState.Disconnected)
            .Subscribe(_ => ConnectionLost(current, new IOException("The bluetooth connection was lost.")), exception => Fail(current, exception)));
        events.Add(selected.WhenConnectionFailed().Subscribe(
            exception =>
        {
            if (Volatile.Read(ref connectIssued))
            {
                ConnectionLost(current, exception);
            }
        }, exception => Fail(current, exception)));
        Volatile.Write(ref connectIssued, true);
        await selected.ConnectAsync(new ConnectionConfig(AutoConnect: false), cancellationToken).ConfigureAwait(false);
        var protocolInfo = await selected.ReadCharacteristic(BleProtocolConstants.Service, BleProtocolConstants.ProtocolInfo).ToTask().ConfigureAwait(false);
        ValidateProtocolInfo(protocolInfo.Data);
        await SubscribeAsync(current, selected, owned, BleProtocolConstants.Response, "response", true, HandleResponse, cancellationToken).ConfigureAwait(false);
        await SubscribeAsync(current, selected, owned, BleProtocolConstants.SensorStream, "sensor stream", false, HandleMotionObservation, cancellationToken).ConfigureAwait(false);
        lock (sync)
        {
            leaseMaintenance = MaintainLeaseAsync(current, acquired, stopping.Token);
        }

        await acquired.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        await StartMotionStreamAsync(current, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected override Task WriteCommandAsync(DeviceCommand command, CancellationToken cancellationToken)
    {
        var path = command.Control switch
        {
            DeviceControl.Pattern => "motion.pattern",
            DeviceControl.Speed => "motion.speed",
            DeviceControl.Stroke => "motion.stroke",
            DeviceControl.Depth => "motion.depth",
            DeviceControl.Sensation => "motion.sensation",
            _ => throw new ArgumentOutOfRangeException(nameof(command), command.Control, "Unknown firmware control."),
        };
        long current;
        lock (sync)
        {
            current = generation;
        }

        return SendRequestAsync(current, "setting.write", path, new Dictionary<string, int> { ["value"] = command.Value }, leased: true, cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Selects the stroke engine. Entering it resets the firmware's controls, so a mode change waits for the next
    /// observation. The firmware homes first when needed and holds a preflight until the speed knob reads zero;
    /// both report not ready.
    /// </remarks>
    protected override async Task<bool> PreparePatternAsync(CancellationToken cancellationToken)
    {
        long current;
        lock (sync)
        {
            current = generation;
        }

        var response = await SendRequestAsync(current, "target.set", "target.strokeEngine", null, leased: true, cancellationToken).ConfigureAwait(false);
        if (response.StateAfter is not ("strokeEngine.idle" or "strokeEngine.pattern"))
        {
            return false;
        }

        if (response.StateBefore != response.StateAfter)
        {
            UpdateState(await ReadStateAsync(cancellationToken).ConfigureAwait(false));
        }

        return true;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Waits for the next valid motion-stream observation; cancellation abandons only the wait.
    /// </remarks>
    protected override async Task<DeviceState> ReadStateAsync(CancellationToken cancellationToken)
    {
        var read = new TaskCompletionSource<DeviceState>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (sync)
        {
            if (!active || streamId == 0)
            {
                throw new IOException("The motion stream is not active.");
            }

            liveRead = read;
        }

        try
        {
            return await read.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (sync)
            {
                if (liveRead == read)
                {
                    liveRead = null;
                }
            }
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Stops the stream and releases the lease only while the connection is still usable; the firmware also
    /// releases both when the bluetooth connection closes. A release failure propagates after teardown completes.
    /// </remarks>
    protected override async Task DisconnectCoreAsync()
    {
        IPeripheral? selected;
        long current;
        CancellationTokenSource? stopping;
        Task maintenance;
        CompositeDisposable owned;
        CompositeDisposable events;
        bool acquiring;
        lock (sync)
        {
            selected = peripheral;
            if (selected is null)
            {
                return;
            }

            current = generation;
            stopping = leaseStopping;
            maintenance = leaseMaintenance;
            owned = subscriptions;
            events = lifecycle;
            acquiring = lease == 0;
        }

        var closed = new IOException("The bluetooth connection was closed.");
        if (acquiring)
        {
            // Without a lease, an unanswered acquisition would otherwise wait forever; its GATT write still settles.
            Abandon(current, closed);
        }

        if (stopping is not null)
        {
            await stopping.CancelAsync().ConfigureAwait(false);
        }

        await maintenance.ConfigureAwait(false);
        bool leased;
        bool streaming;
        lock (sync)
        {
            leased = active && lease != 0;
            streaming = active && streamId != 0;
        }

        ExceptionDispatchInfo? releaseFailure = null;
        if (leased && selected.Status == ConnectionState.Connected)
        {
            try
            {
                if (streaming)
                {
                    await SendRequestAsync(current, "stream.stop", null, null, leased: true, CancellationToken.None).ConfigureAwait(false);
                }

                await SendRequestAsync(current, "control.release", null, null, leased: true, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                releaseFailure = ExceptionDispatchInfo.Capture(exception);
            }
        }

        Abandon(current, closed);
        owned.Dispose();
        TaskCompletionSource lost;
        lock (sync)
        {
            // An earlier loss acknowledgement does not cover a reconnection made by Shiny's notification subscriptions.
            lost = disconnected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        selected.CancelConnection();
        if (selected.Status == ConnectionState.Disconnected)
        {
            lost.TrySetResult();
        }

        await lost.Task.ConfigureAwait(false);
        events.Dispose();
        Owners.Remove(selected);
        lock (sync)
        {
            peripheral = null;
            leaseStopping = null;
            leaseMaintenance = Task.CompletedTask;
        }

        stopping?.Dispose();
        releaseFailure?.Throw();
    }

    private static void ValidateProtocolInfo(byte[]? data)
    {
        try
        {
            using var document = JsonDocument.Parse(data ?? []);
            if (TryGetString(document.RootElement, "protocol", out var protocol)
                && protocol == "rad-ble"
                && TryGetUInt32(document.RootElement, "version", out var version)
                && version == 1)
            {
                return;
            }
        }
        catch (JsonException)
        {
        }

        throw new InvalidDataException("The device does not report RAD BLE protocol version 1.");
    }

    private static bool TryGetUInt32(JsonElement element, string name, out uint value)
    {
        value = 0;
        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetUInt32(out value);
    }

    private static bool TryGetString(JsonElement element, string name, [NotNullWhen(true)] out string? value)
    {
        value = element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.String
                ? property.GetString()
                : null;
        return value is not null;
    }

    private static TimeSpan ReadLeaseLifetime(JsonElement result) => TryGetUInt32(result, "ttlMs", out var milliseconds) && milliseconds != 0
        ? TimeSpan.FromMilliseconds(milliseconds)
        : throw new InvalidDataException("The device did not report a positive control lease lifetime.");

    private async Task SubscribeAsync(
        long current,
        IPeripheral selected,
        CompositeDisposable owned,
        string characteristic,
        string name,
        bool indicate,
        Action<long, byte[]?> handler,
        CancellationToken cancellationToken)
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        owned.Add(selected.WhenCharacteristicSubscriptionChanged(BleProtocolConstants.Service, characteristic).Subscribe(
            info =>
        {
            if (info.IsNotifying)
            {
                ready.TrySetResult();
            }
            else if (!ready.TrySetException(new IOException($"The bluetooth {name} subscription could not be enabled.")))
            {
                Fail(current, new IOException($"The bluetooth {name} subscription was lost."));
            }
        }, exception =>
        {
            ready.TrySetException(exception);
            Fail(current, exception);
        }));
        owned.Add(selected.NotifyCharacteristic(BleProtocolConstants.Service, characteristic, indicate).Subscribe(
            result => handler(current, result.Data),
            exception =>
            {
                ready.TrySetException(exception);
                Fail(current, exception);
            },
            () => Fail(current, new IOException($"The bluetooth {name} subscription ended."))));
        await ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<Response> SendRequestAsync(
        long current,
        string operation,
        string? path,
        IReadOnlyDictionary<string, int>? arguments,
        bool leased,
        CancellationToken cancellationToken)
    {
        var response = new TaskCompletionSource<Response>(TaskCreationOptions.RunContinuationsAsynchronously);
        var priority = operation == "setting.write" && path == "motion.speed"
            && arguments is not null && arguments.TryGetValue("value", out var value) && value == 0
            && !cancellationToken.CanBeCanceled
            ? new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
            : null;
        IPeripheral selected;
        uint id;
        uint? token;
        lock (sync)
        {
            if (!IsCurrent(current))
            {
                throw new IOException("The bluetooth connection is no longer active.");
            }

            if (leased && lease == 0)
            {
                throw new InvalidOperationException("No control lease is held.");
            }

            do
            {
                id = unchecked(++nextRequestId);
            }
            while (id == 0 || requests.ContainsKey(id));

            token = leased ? lease : null;
            selected = peripheral!;
            requests.Add(id, response);
            if (priority is not null)
            {
                stopTransfer = priority;
            }
        }

        try
        {
            var payload = Request.Encode(id, operation, path, arguments, token);
            if (payload.Length > MaximumMessageBytes)
            {
                throw new InvalidOperationException($"The {operation} request exceeds {MaximumMessageBytes} bytes.");
            }

            for (var attempt = 1; ; attempt++)
            {
                await WriteRequestAsync(current, selected, payload, priority, cancellationToken).ConfigureAwait(false);
                try
                {
                    return await response.Task.WaitAsync(ResponseTimeout, timeProvider, cancellationToken).ConfigureAwait(false);
                }
                catch (TimeoutException) when (attempt < MaximumRequestAttempts)
                {
                    // The firmware replays a cached response for a repeated identifier and re-executes one it could not cache.
                    logger.RequestResent(operation);
                }
                catch (TimeoutException)
                {
                    var failure = new IOException($"The device did not answer the {operation} request.");
                    Fail(current, failure);
                    throw failure;
                }
            }
        }
        finally
        {
            priority?.TrySetResult();
            lock (sync)
            {
                if (requests.TryGetValue(id, out var pending) && pending == response)
                {
                    requests.Remove(id);
                }
            }
        }
    }

    [SuppressMessage("Usage", "CA1849", Justification = "Shiny's Async wrapper adds a timeout that abandons issued GATT I/O; the raw observable is awaited to settlement.")]
    private async Task WriteRequestAsync(long current, IPeripheral selected, byte[] payload, TaskCompletionSource? priority, CancellationToken cancellationToken)
    {
        while (true)
        {
            await writes.WaitAsync(cancellationToken).ConfigureAwait(false);
            Task? higherPriority = null;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                lock (sync)
                {
                    if (!IsCurrent(current))
                    {
                        throw new IOException("The bluetooth connection is no longer active.");
                    }

                    if (priority is null && stopTransfer is { Task.IsCompleted: false })
                    {
                        higherPriority = stopTransfer.Task;
                    }
                }

                if (higherPriority is null)
                {
                    await selected
                        .WriteCharacteristic(BleProtocolConstants.Service, BleProtocolConstants.Request, payload, withResponse: true)
                        .ToTask()
                        .ConfigureAwait(false);
                    return;
                }
            }
            finally
            {
                writes.Release();
                priority?.TrySetResult();
            }

            await higherPriority.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private void HandleResponse(long current, byte[]? data)
    {
        uint id;
        string? stage;
        bool ok;
        string? code;
        string? message;
        string? stateBefore;
        string? stateAfter;
        JsonElement result;
        try
        {
            if (data is null || data.Length > MaximumMessageBytes)
            {
                throw new InvalidDataException("A RAD response must contain at most 509 bytes.");
            }

            using var document = JsonDocument.Parse(data);
            var root = document.RootElement;
            if (!TryGetUInt32(root, "v", out var version)
                || version != 1
                || !TryGetUInt32(root, "id", out id)
                || !TryGetString(root, "stage", out stage)
                || stage is not ("accepted" or "completed" or "failed")
                || !root.TryGetProperty("ok", out var status)
                || status.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                throw new InvalidDataException("A RAD response requires v=1, an id, a known stage, and ok.");
            }

            ok = status.GetBoolean();
            TryGetString(root, "code", out code);
            TryGetString(root, "message", out message);
            TryGetString(root, "stateBefore", out stateBefore);
            TryGetString(root, "stateAfter", out stateAfter);
            result = root.TryGetProperty("result", out var value) ? value.Clone() : default;
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
            // An uncorrelated response cannot complete a request; its sender resends after the response timeout.
            logger.ResponseDiscarded(exception);
            return;
        }

        TaskCompletionSource<Response>? pending;
        lock (sync)
        {
            if (!IsCurrent(current) || !requests.TryGetValue(id, out pending))
            {
                return;
            }
        }

        // Accepted requests remain pending until a terminal stage.
        if (stage == "completed" && ok)
        {
            pending.TrySetResult(new(result, stateBefore, stateAfter));
        }
        else if (stage != "accepted")
        {
            pending.TrySetException(new IOException($"The device rejected the request: {code ?? "unknown"}{(message is null ? string.Empty : $" ({message})")}."));
        }
    }

    private async Task MaintainLeaseAsync(long current, TaskCompletionSource acquired, CancellationToken stopping)
    {
        TimeSpan lifetime;
        DateTimeOffset requested;
        try
        {
            requested = timeProvider.GetUtcNow();
            var result = (await SendRequestAsync(current, "control.acquire", null, null, leased: false, stopping).ConfigureAwait(false)).Result;
            if (!TryGetUInt32(result, "lease", out var token) || token == 0)
            {
                throw new InvalidDataException("The device did not return a control lease.");
            }

            lifetime = ReadLeaseLifetime(result);
            lock (sync)
            {
                if (!IsCurrent(current))
                {
                    throw new IOException("The bluetooth connection is no longer active.");
                }

                lease = token;
            }

            acquired.TrySetResult();
        }
        catch (Exception exception)
        {
            acquired.TrySetException(exception);
            return;
        }

        while (true)
        {
            var expires = requested + lifetime;
            try
            {
                var delay = requested + (lifetime / 2) - timeProvider.GetUtcNow();
                await Task.Delay(delay > TimeSpan.Zero ? delay : TimeSpan.Zero, timeProvider, stopping).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            requested = timeProvider.GetUtcNow();
            var renewal = SendRequestAsync(current, "control.renew", null, null, leased: true, stopping);
            try
            {
                var remaining = expires - timeProvider.GetUtcNow();
                // Issued renewal is bounded by lease expiry, not by disconnect.
                lifetime = ReadLeaseLifetime((await renewal.WaitAsync(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, timeProvider, CancellationToken.None).ConfigureAwait(false)).Result);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                Fail(current, exception is TimeoutException
                    ? new IOException("The control lease expired before renewal completed; control ownership is lost.")
                    : new IOException("Control lease renewal failed; control ownership is lost.", exception));
                try
                {
                    await renewal.ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // The connection failure above already reports this result.
                }

                return;
            }
        }
    }

    private async Task StartMotionStreamAsync(long current, CancellationToken cancellationToken)
    {
        var result = (await SendRequestAsync(current, "stream.start", "motion.speed", null, leased: true, cancellationToken).ConfigureAwait(false)).Result;
        if (!TryGetUInt32(result, "streamId", out var id)
            || id is 0 or > byte.MaxValue
            || !TryGetString(result, "surface", out var surface)
            || surface != "motion")
        {
            throw new InvalidDataException("The device did not start a motion stream.");
        }

        lock (sync)
        {
            if (!IsCurrent(current))
            {
                throw new IOException("The bluetooth connection is no longer active.");
            }

            streamId = (byte)id;
        }
    }

    private void HandleMotionObservation(long current, byte[]? data)
    {
        byte expectedStream;
        lock (sync)
        {
            if (!IsCurrent(current) || streamId == 0)
            {
                return;
            }

            expectedStream = streamId;
        }

        if (data is null || data.Length <= StreamHeaderBytes || data.Length > MaximumMessageBytes)
        {
            Fail(current, new InvalidDataException("The device sent a malformed sensor stream frame."));
            return;
        }

        if (data[1] != expectedStream)
        {
            // Frames from an earlier stream can arrive after it is replaced.
            return;
        }

        var flags = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(2));
        var length = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(6));
        if (data[0] != 1 || (flags & 1) == 0 || length != data.Length - StreamHeaderBytes)
        {
            Fail(current, new InvalidDataException("The device sent a malformed sensor stream frame."));
            return;
        }

        DeviceState observation;
        try
        {
            observation = DeviceState.FromBytes(data.AsMemory(StreamHeaderBytes));
        }
        catch (InvalidDataException exception)
        {
            // The firmware can emit truncated or incomplete payloads in valid frames; wait for the next observation.
            logger.ObservationDiscarded(exception);
            return;
        }

        TaskCompletionSource<DeviceState>? read;
        lock (sync)
        {
            if (!IsCurrent(current))
            {
                return;
            }

            read = liveRead;
            liveRead = null;
        }

        if (read is null)
        {
            lock (sync)
            {
                if (IsCurrent(current))
                {
                    UpdateState(observation);
                }
            }
        }
        else
        {
            read.TrySetResult(observation);
        }
    }

    private bool IsCurrent(long current) => active && generation == current;

    private void ConnectionLost(Exception exception)
    {
        long current;
        lock (sync)
        {
            current = generation;
        }

        ConnectionLost(current, exception);
    }

    private void ConnectionLost(long current, Exception exception)
    {
        TaskCompletionSource lost;
        lock (sync)
        {
            if (current != generation)
            {
                return;
            }

            lost = disconnected;
        }

        lost.TrySetResult();
        Fail(current, exception);
    }

    private void Fail(long current, Exception exception)
    {
        CompositeDisposable owned;
        lock (sync)
        {
            if (!IsCurrent(current))
            {
                return;
            }

            owned = subscriptions;
        }

        if (Abandon(current, exception))
        {
            // Live notification subscriptions make Shiny reconnect; a failed connection must not be revived.
            owned.Dispose();
            lock (sync)
            {
                if (current == generation)
                {
                    SetFaulted(exception);
                }
            }
        }
    }

    private bool Abandon(long current, Exception exception)
    {
        TaskCompletionSource<Response>[] pending;
        TaskCompletionSource<DeviceState>? read;
        lock (sync)
        {
            if (!IsCurrent(current))
            {
                return false;
            }

            active = false;
            pending = [.. requests.Values];
            requests.Clear();
            read = liveRead;
            liveRead = null;
        }

        foreach (var request in pending)
        {
            request.TrySetException(exception);
        }

        read?.TrySetException(exception);
        return true;
    }

    private readonly record struct Response(JsonElement Result, string? StateBefore, string? StateAfter);
}
