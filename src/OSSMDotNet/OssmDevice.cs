using Microsoft.Extensions.Options;

namespace OSSMDotNet;

/// <summary>
/// Represents an OSSM device running the supported stock firmware.
/// </summary>
/// <remarks>
/// BLE transport integration is intentionally deferred. This implementation
/// reports unavailable state and rejects writes rather than simulating success.
/// </remarks>
public sealed class OssmDevice : OssmDeviceBase
{
    private static readonly NotSupportedException BleUnavailable = new(
        "Stock-firmware BLE transport is not implemented.");

    /// <summary>
    /// Initializes a new instance of the <see cref="OssmDevice"/> class.
    /// </summary>
    /// <param name="options">The validated stock-device options.</param>
    /// <param name="timeProvider">The monotonic and UTC time source.</param>
    /// <param name="logger">The operational logger.</param>
    public OssmDevice(
        IOptions<OssmDeviceOptions> options,
        TimeProvider timeProvider,
        ILogger<OssmDevice> logger)
        : this(ValidateOptions(options), timeProvider, logger)
    {
    }

    private OssmDevice(
        OssmDeviceOptions options,
        TimeProvider timeProvider,
        ILogger<OssmDevice> logger)
        : base(options, timeProvider, logger)
    {
    }

    /// <inheritdoc />
    protected override async Task RunTransportAsync(CancellationToken cancellationToken)
    {
        PublishDisconnected();
        PublishError(BleUnavailable);
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected override Task DispatchMotionAsync(
        MotionSettings motionSettings,
        CancellationToken cancellationToken) => Task.FromException(BleUnavailable);

    /// <inheritdoc />
    protected override bool IsExpectedObservation(
        MotionSettings requested,
        DeviceState? previous,
        DeviceState observed) => DeviceState.IsSpeedMatch(
            LastRequestedMotion,
            previous,
            requested,
            observed);

    private static OssmDeviceOptions ValidateOptions(IOptions<OssmDeviceOptions>? options)
    {
        return options?.Value ?? throw new ArgumentNullException(nameof(options));
    }
}
