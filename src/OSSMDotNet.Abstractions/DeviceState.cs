namespace OSSMDotNet.Abstractions;

/// <summary>
/// Represents a state notification from an OSSM device.
/// </summary>
public sealed record DeviceState(
    DateTime Timestamp,
    MotionSettings MotionSettings,
    Guid SessionId)
{
    /// <summary>
    /// Determines whether an effective speed is consistent with the requested speed and prior feedback.
    /// </summary>
    /// <param name="previousRequestedSpeed">The previous requested speed, when available.</param>
    /// <param name="previousObservedSpeed">The previous effective speed, when available.</param>
    /// <param name="requestedSpeed">The newly requested speed.</param>
    /// <param name="observedSpeed">The newly observed effective speed.</param>
    /// <returns><see langword="true"/> when a knob percentage can explain both observations.</returns>
    public static bool IsSpeedMatch(
        int? previousRequestedSpeed,
        int? previousObservedSpeed,
        int requestedSpeed,
        int observedSpeed)
    {
        if (requestedSpeed == 0)
        {
            return observedSpeed == 0;
        }

        if (observedSpeed < 0 || observedSpeed > requestedSpeed)
        {
            return false;
        }

        if (previousRequestedSpeed is not > 0 || previousObservedSpeed is null)
        {
            return true;
        }

        for (var knobPercentage = 0; knobPercentage <= 100; knobPercentage++)
        {
            if ((previousRequestedSpeed.Value * knobPercentage / 100) == previousObservedSpeed &&
                (requestedSpeed * knobPercentage / 100) == observedSpeed)
            {
                return true;
            }
        }

        return false;
    }
}
