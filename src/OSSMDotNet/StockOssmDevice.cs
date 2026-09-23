using Microsoft.Extensions.Options;
using OSSMDotNet.Models;

namespace OSSMDotNet;

/// <summary>
/// Represents an OSSM device running the supported stock firmware.
/// </summary>
public sealed class StockOssmDevice : DeviceBase
{
    /// <summary>
    /// Initializes a new instance of the <see cref="StockOssmDevice"/> class.
    /// </summary>
    /// <param name="options">The validated stock-device options.</param>
    /// <param name="timeProvider">The monotonic and UTC time source.</param>
    /// <param name="logger">The operational logger.</param>
    public StockOssmDevice(
        IOptions<StockOssmDeviceOptions> options,
        TimeProvider timeProvider,
        ILogger<StockOssmDevice> logger)
        : base(
            options?.Value ?? throw new ArgumentNullException(nameof(options)),
            timeProvider,
            logger)
    {
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
        => IsCurrentStateCongruent(
            previousRequestedSpeed,
            previousStateSpeed,
            requestedSpeed,
            stateSpeed);
}
