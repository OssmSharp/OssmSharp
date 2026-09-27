#if WINDOWS || MACOS
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Threading.Channels;
using OssmSharp.Device.Primitives;
using OssmSharp.Device.Primitives.Models;
using OssmSharp.Device.Stock;
using OssmSharp.Device.Stock.Models;
#endif

namespace OssmSharp.Cli.IntegrationTests;

#if WINDOWS || MACOS
internal sealed class NativeStockTestDevice : IDeviceTransport, IDeviceConnection
{
    private static readonly byte[] Identity =
        "a4:cf:12:34:56:78;ESP32-S3;1;0123456789abcdef0123456789abcdef;1.0.34"u8.ToArray();
    private static readonly byte[] Patterns = "[{\"name\":\"Simple Stroke\",\"idx\":0}]"u8.ToArray();
    private readonly DeviceIdentifier _selected;
    private readonly Channel<byte[]> _notifications = Channel.CreateUnbounded<byte[]>();
    private readonly ConcurrentQueue<string> _commands = new();
    private readonly object _stateGate = new();
    private int _speed;
    private int _stroke;
    private int _depth;
    private int _knobLimitingPreparations;
    private int _disposeCount;
    private int _timestamp;

    internal NativeStockTestDevice(DeviceIdentifier selected, int initialStroke, int initialDepth)
    {
        _selected = selected;
        _stroke = initialStroke;
        _depth = initialDepth;
        Profile = new StockGattProfile(
            selected.Id,
            StockFirmwareProfile.Service,
            [
                StockFirmwareProfile.Identity,
                StockFirmwareProfile.Command,
                StockFirmwareProfile.KnobConfiguration,
                StockFirmwareProfile.State,
                StockFirmwareProfile.Patterns,
                StockFirmwareProfile.PatternData,
            ],
            Identity,
            Patterns,
            _notifications.Reader);
        _notifications.Writer.TryWrite(CreateState());
    }

    internal StockGattProfile Profile { get; }

    internal DeviceIdentifier? LastSelected { get; private set; }

    internal int KnobLimitingPreparations => Volatile.Read(ref _knobLimitingPreparations);

    internal int DisposeCount => Volatile.Read(ref _disposeCount);

    public Task<IDeviceConnection> ConnectAsync(
        DeviceIdentifier selected,
        Guid service,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!ReferenceEquals(selected, _selected) || service != StockFirmwareProfile.Service)
            throw new InvalidOperationException("Stock transport received a different selected identifier.");

        LastSelected = selected;
        return Task.FromResult<IDeviceConnection>(this);
    }

    public Task<StockGattProfile> ReadProfileAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Profile);
    }

    public async Task WriteCommandAsync(Request request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var payload = request.ToPayload();
        _commands.Enqueue(payload);
        lock (_stateGate)
        {
            switch (request.Setting)
            {
                case "speed":
                    _speed = request.Value;
                    break;
                case "stroke":
                    _stroke = request.Value;
                    break;
                case "depth":
                    _depth = request.Value;
                    break;
            }
        }

        await _notifications.Writer.WriteAsync(CreateState(), cancellationToken).ConfigureAwait(false);
    }

    public Task EnableKnobLimitingAsync(
        CancellationToken revocationToken,
        CancellationToken settlementToken)
    {
        revocationToken.ThrowIfCancellationRequested();
        settlementToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _knobLimitingPreparations);
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Interlocked.Increment(ref _disposeCount);
        _notifications.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }

    internal string[] GetCommands() => _commands.ToArray();

    private byte[] CreateState()
    {
        int speed;
        int stroke;
        int depth;
        lock (_stateGate)
        {
            speed = _speed;
            stroke = _stroke;
            depth = _depth;
        }

        var effectiveSpeed = speed == 12 ? 10 : speed;
        var timestamp = Interlocked.Increment(ref _timestamp);
        var payload = string.Create(
            CultureInfo.InvariantCulture,
            $$"""{"timestamp":{{timestamp}},"state":"strokeEngine.idle","speed":{{effectiveSpeed}},"stroke":{{stroke}},"sensation":50,"depth":{{depth}},"buffer":0,"pattern":0,"position":0,"sessionId":""}""");
        return Encoding.UTF8.GetBytes(payload);
    }
}
#endif
