namespace OSSMDotNet.Abstractions;

/// <summary>
/// Represents one device state report.
/// </summary>
public sealed record DeviceState
{
    /// <summary>
    /// Gets the original UTC sample timestamp.
    /// </summary>
    public required DateTime Timestamp { get; init; }

    /// <summary>
    /// Gets the reported settings. Speed is an effective percentage and position is in millimetres.
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
}
