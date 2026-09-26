using Microsoft.Extensions.Options;
using OssmSharp.Device.Runtime;
using OssmSharp.Device.Stock.Models;
using System.ComponentModel.DataAnnotations;
using System.Text;

namespace OssmSharp.Device.Stock;

/// <summary>
/// Represents an OSSM device running the supported stock firmware.
/// </summary>
public sealed class StockOssmDevice : DeviceBase
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
    private readonly TimeProvider _timeProvider;
    private readonly IStockBleTransport? _transport;
    private readonly string? _peripheralId;
    private readonly string? _expectedWifiMac;

    /// <summary>
    /// Initializes a new instance of the <see cref="StockOssmDevice"/> class.
    /// </summary>
    /// <param name="options">The validated stock-device options.</param>
    /// <param name="timeProvider">The monotonic and UTC time source.</param>
    /// <param name="logger">The operational logger.</param>
    public StockOssmDevice(
        IOptions<DeviceRuntimeOptions> options,
        TimeProvider timeProvider,
        ILogger<StockOssmDevice> logger)
        : base(
            options?.Value ?? throw new ArgumentNullException(nameof(options)),
            timeProvider,
            logger)
    {
        _timeProvider = timeProvider;
    }

#if WINDOWS
    /// <summary>Creates a Stock adapter for a specifically enrolled Windows peripheral.</summary>
    public StockOssmDevice(
        IOptions<DeviceRuntimeOptions> options,
        IOptions<StockBleOptions> stockOptions,
        Shiny.BluetoothLE.IBleManager manager,
        TimeProvider timeProvider,
        ILogger<StockOssmDevice> logger)
        : this(
            options,
            timeProvider,
            logger,
            stockOptions?.Value.PeripheralId ?? throw new ArgumentNullException(nameof(stockOptions)),
            stockOptions.Value.ExpectedWifiMac,
            new WindowsStockBleTransport(manager ?? throw new ArgumentNullException(nameof(manager))))
    {
        Validator.ValidateObject(stockOptions.Value, new ValidationContext(stockOptions.Value), true);
    }
#endif

    internal StockOssmDevice(
        IOptions<DeviceRuntimeOptions> options,
        TimeProvider timeProvider,
        ILogger<StockOssmDevice> logger,
        string peripheralId,
        string expectedWifiMac,
        IStockBleTransport transport)
        : this(options, timeProvider, logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(peripheralId);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedWifiMac);
        _peripheralId = peripheralId;
        _expectedWifiMac = expectedWifiMac;
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
    }

    /// <summary>
    /// Checks whether a reported effective speed is consistent with the requested speed and prior knob limit.
    /// </summary>
    /// <param name="previousRequestedSpeed">The preceding requested percentage, if known.</param>
    /// <param name="previousStateSpeed">The preceding reported effective percentage, if known.</param>
    /// <param name="requestedSpeed">The current requested percentage.</param>
    /// <param name="stateSpeed">The current reported effective percentage.</param>
    /// <returns>Whether the reported speed can be attributed to this write, allowing firmware truncation.</returns>
    internal static bool IsCurrentStateCongruent(
        int? previousRequestedSpeed,
        int? previousStateSpeed,
        int requestedSpeed,
        int stateSpeed)
    {
        if (requestedSpeed == 0)
        {
            return stateSpeed == 0;
        }

        if (stateSpeed < 0 || stateSpeed > requestedSpeed)
        {
            return false;
        }

        if (previousRequestedSpeed is not > 0 || previousStateSpeed is null)
        {
            return true;
        }

        if (previousStateSpeed < 0 || previousStateSpeed > previousRequestedSpeed)
        {
            return false;
        }

        if (previousStateSpeed == previousRequestedSpeed || stateSpeed == requestedSpeed)
        {
            return previousStateSpeed == previousRequestedSpeed && stateSpeed == requestedSpeed;
        }

        return (long)previousStateSpeed.Value * requestedSpeed <
               (long)(stateSpeed + 1) * previousRequestedSpeed.Value &&
               (long)stateSpeed * previousRequestedSpeed.Value <
               (long)(previousStateSpeed.Value + 1) * requestedSpeed;
    }

    /// <inheritdoc />
    protected override async Task ConnectAsync(CancellationToken cancellationToken)
    {
        if (_transport is null)
        {
            throw new NotSupportedException("Stock-firmware BLE transport is not configured.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            await using var connection = await _transport.ConnectAsync(
                _peripheralId!, StockFirmwareProfile.Service, timeout.Token).ConfigureAwait(false);
            var profile = await connection.ReadProfileAsync(timeout.Token).ConfigureAwait(false);
            if (!string.Equals(profile.PeripheralId, _peripheralId, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The selected peripheral differs from the enrolled locator.");
            }

            var device = StockFirmwareProfile.Verify(
                profile.PeripheralId, _expectedWifiMac!, profile.Service, profile.Characteristics,
                profile.Identity, profile.Patterns);
            if (profile.Notifications.Completion.IsCompleted)
            {
                throw new IOException("The stock BLE connection ended during identification.");
            }

            PublishConnected(device);
            timeout.CancelAfter(Timeout.InfiniteTimeSpan);

            await foreach (var payload in profile.Notifications.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                if (payload.Length is 0 or > 4096)
                {
                    throw new InvalidDataException("The stock state notification exceeds its bounds.");
                }

                var state = StateResponse.Parse(
                    StrictUtf8.GetString(payload), _timeProvider.GetUtcNow().UtcDateTime);
                PublishState(state.ToDeviceState(), null);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Stock BLE discovery or identification timed out.");
        }
        catch (Exception exception) when (exception is not (
            OperationCanceledException or IOException or InvalidDataException or TimeoutException or UnauthorizedAccessException))
        {
            throw new IOException("Stock BLE operation failed.", exception);
        }
    }

    /// <inheritdoc />
    protected override Task DispatchMotionAsync(
        MotionSettings motionSettings,
        CancellationToken cancellationToken) => Task.FromException(
            new NotSupportedException("Stock-firmware BLE transport is not implemented."));

    protected override bool IsExpectedState(
        int? previousRequestedSpeed,
        int? previousStateSpeed,
        int requestedSpeed,
        int stateSpeed)
        => IsCurrentStateCongruent(
            previousRequestedSpeed,
            previousStateSpeed,
            requestedSpeed,
            stateSpeed);
}
