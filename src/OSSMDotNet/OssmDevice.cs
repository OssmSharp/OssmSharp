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
        : base(
            options?.Value ?? throw new ArgumentNullException(nameof(options)),
            timeProvider,
            logger)
    {
    }

    /// <inheritdoc />
    protected override Task ConnectAsync(CancellationToken cancellationToken) =>
        Task.FromException(new NotSupportedException("Stock-firmware BLE transport is not implemented."));

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
        => IsExpectedStateImpl(
            previousRequestedSpeed,
            previousStateSpeed,
            requestedSpeed,
            stateSpeed);

    /// <summary>
    /// Determines whether the reported speed is consistent with the previously-requested.
    /// </summary>
    /// <param name="previousRequestedSpeed">The requested speed that settled before the pending request was registered.</param>
    /// <param name="previousStateSpeed">The previous reported speed, when available.</param>
    /// <param name="requestedSpeed">The pending requested speed.</param>
    /// <param name="stateSpeed">The reported speed.</param>
    /// <returns><see langword="true"/> when the reported speed is consistent with the request.</returns>
    public static bool IsExpectedStateImpl(
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

        for (var knobPercentage = 0; knobPercentage <= 100; knobPercentage++)
        {
            if ((previousRequestedSpeed.Value * knobPercentage / 100) == previousStateSpeed &&
                (requestedSpeed * knobPercentage / 100) == stateSpeed)
            {
                return true;
            }
        }

        return false;
    }

}
