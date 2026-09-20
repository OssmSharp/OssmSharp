namespace OSSMDotNet.Abstractions;

/// <summary>
/// Represents the latest device observation and retained requested motion.
/// </summary>
public sealed record DeviceState
{
    /// <summary>
    /// Gets the original UTC sample timestamp.
    /// </summary>
    public required DateTime Timestamp { get; init; }

    /// <summary>
    /// Gets the observed settings. Speed is an effective percentage and position is in millimetres.
    /// </summary>
    public required MotionSettings MotionSettings { get; init; }

    /// <summary>
    /// Gets the device session identifier.
    /// </summary>
    public required Guid SessionId { get; init; }

    /// <summary>
    /// Gets the directly reported signed velocity in millimetres per second, or <see langword="null"/> when not reported.
    /// A supplied value must be finite; zero is a reported value, not absence. Velocity must not be derived from speed percentages.
    /// </summary>
    public double? VelocityMillimetresPerSecond { get; init; }

    /// <summary>
    /// Gets the last successfully settled requested settings, or <see langword="null"/> before any write settles.
    /// </summary>
    public MotionSettings? RequestedMotion { get; init; }

    /// <summary>
    /// Gets the freshness of the retained observation; successful writes do not refresh it.
    /// </summary>
    public ObservationFreshness ObservationFreshness { get; init; } = ObservationFreshness.Unavailable;

    /// <summary>
    /// Determines whether an observed state can represent the requested state without prior feedback.
    /// </summary>
    /// <param name="requested">The requested state.</param>
    /// <param name="observed">The observed state.</param>
    /// <returns><see langword="true"/> when the observed speed can result from the requested speed.</returns>
    public static bool IsSpeedMatch(DeviceState requested, DeviceState observed)
    {
        ArgumentNullException.ThrowIfNull(requested);
        ArgumentNullException.ThrowIfNull(observed);
        return requested.MotionSettings is not null && observed.MotionSettings is not null && IsSpeedMatch(
            null,
            null,
            requested.MotionSettings.Speed,
            observed.MotionSettings.Speed);
    }

    /// <summary>
    /// Determines whether an observed state is consistent with requested settings and prior feedback.
    /// </summary>
    /// <param name="previousRequested">The previous requested settings, when available.</param>
    /// <param name="previousObserved">The previous observed state, when available.</param>
    /// <param name="requested">The newly requested settings.</param>
    /// <param name="observed">The newly observed state.</param>
    /// <returns><see langword="true"/> when a knob percentage can explain both observations.</returns>
    public static bool IsSpeedMatch(
        MotionSettings? previousRequested,
        DeviceState? previousObserved,
        MotionSettings requested,
        DeviceState observed)
    {
        ArgumentNullException.ThrowIfNull(requested);
        ArgumentNullException.ThrowIfNull(observed);
        return observed.MotionSettings is not null && IsSpeedMatch(
            previousRequested?.Speed,
            previousObserved?.MotionSettings?.Speed,
            requested.Speed,
            observed.MotionSettings.Speed);
    }

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
