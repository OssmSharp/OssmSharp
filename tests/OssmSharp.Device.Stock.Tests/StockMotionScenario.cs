using System.Threading.Channels;
using FastMoq;
using FastMoq.Extensions;
using FastMoq.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OssmSharp.Device.Runtime;

namespace OssmSharp.Device.Stock.Tests;

internal sealed class StockMotionScenario : IAsyncDisposable
{
    private static readonly byte[] Identity =
        "a4:cf:12:34:56:78;ESP32-S3;1;0123456789abcdef0123456789abcdef;1.0.34"u8.ToArray();
    private static readonly byte[] Patterns = "[{\"name\":\"Simple Stroke\",\"idx\":0}]"u8.ToArray();
    private readonly Mocker _mocks = new();
    private readonly Channel<byte[]> _notifications = Channel.CreateUnbounded<byte[]>();
    private int _firmwareTimestamp;

    private StockMotionScenario(IStockQualificationResolver qualification, TimeProvider clock)
    {
        Connection = new RecordingStockConnection(
            new StockGattProfile(
                "enrolled", StockFirmwareProfile.Service,
                [StockFirmwareProfile.Identity, StockFirmwareProfile.Command, StockFirmwareProfile.KnobConfiguration,
                    StockFirmwareProfile.State, StockFirmwareProfile.Patterns, StockFirmwareProfile.PatternData],
                Identity, Patterns, _notifications.Reader));
        _mocks.AddMethodResultAsync<IDeviceTransport, IDeviceConnection>(
            transport => transport.ConnectAsync(
                FastArg.Any<DeviceIdentifier>(), StockFirmwareProfile.Service, FastArg.Any<CancellationToken>()),
            Connection);
        Device = new StockOssmDevice(
            Options.Create(new DeviceRuntimeOptions()), clock,
            NullLogger<StockOssmDevice>.Instance, new SelectedStockTestIdentifier("enrolled"),
            _mocks.GetRequiredObject<IDeviceTransport>(), qualification);
        Execution = new DeviceExecutionSession(Device, clock);
    }

    public RecordingStockConnection Connection { get; }

    public StockOssmDevice Device { get; }

    public DeviceExecutionSession Execution { get; }

    public async ValueTask DisposeAsync()
    {
        await Execution.DisposeAsync();
        await Device.DisposeAsync();
        _mocks.Dispose();
    }

    internal static StockMotionScenario Create(
        IStockQualificationResolver qualification,
        TimeProvider? clock = null) => new(qualification, clock ?? TimeProvider.System);

    internal static async Task<StockMotionScenario> ConnectAsync(
        IStockQualificationResolver qualification,
        int stroke,
        int depth,
        CancellationToken cancellationToken,
        TimeProvider? clock = null,
        string initialState = "strokeEngine.idle",
        int initialSensation = 50)
    {
        var scenario = Create(qualification, clock);
        try
        {
            var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var reported = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            scenario.Device.ConnectionStateChanged += state =>
            {
                if (state == DeviceConnectionState.Connected)
                    connected.TrySetResult();
            };
            scenario.Device.DeviceStateChanged += state =>
            {
                if (state.IsT0)
                    reported.TrySetResult();
                else
                    reported.TrySetException(state.AsT1);
            };
            await scenario.Device.StartAsync(cancellationToken);
            await connected.Task.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken);
            await scenario.ReportAsync(0, stroke, depth, initialSensation, initialState, cancellationToken);
            await reported.Task.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken);
            return scenario;
        }
        catch
        {
            await scenario.DisposeAsync();
            throw;
        }
    }

    internal bool TryReport(int speed, int stroke, int depth) =>
        _notifications.Writer.TryWrite(State(speed, stroke, depth));

    private async Task ReportAsync(
        int speed,
        int stroke,
        int depth,
        int sensation,
        string state,
        CancellationToken cancellationToken) =>
        await _notifications.Writer.WriteAsync(State(speed, stroke, depth, sensation, state), cancellationToken);

    private byte[] State(
        int speed, int stroke, int depth, int sensation = 50, string state = "strokeEngine.idle") =>
        System.Text.Encoding.UTF8.GetBytes(
            $$"""{"timestamp":{{Interlocked.Increment(ref _firmwareTimestamp)}},"state":"{{state}}","speed":{{speed}},"stroke":{{stroke}},"sensation":{{sensation}},"depth":{{depth}},"buffer":0,"pattern":0,"position":0,"sessionId":""}""");
}
