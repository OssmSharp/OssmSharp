using System.ComponentModel.DataAnnotations;

namespace OSSMDotNet.Abstractions;

/// <summary>
/// Represents one device state report.
/// </summary>
public sealed record DeviceState : IValidatableObject
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

    /// <inheritdoc />
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Timestamp.Kind != DateTimeKind.Utc)
        {
            yield return new ValidationResult(
                "State timestamps must use UTC.",
                [nameof(Timestamp)]);
        }

        if (MotionSettings is null)
        {
            yield return new ValidationResult(
                "Device state must contain motion settings.",
                [nameof(MotionSettings)]);
        }
        else
        {
            var settingsResults = new List<ValidationResult>();
            if (!Validator.TryValidateObject(
                    MotionSettings,
                    new ValidationContext(MotionSettings),
                    settingsResults,
                    validateAllProperties: true))
            {
                yield return new ValidationResult(
                    "The device state contains invalid motion settings.",
                    [nameof(MotionSettings)]);
            }
        }

        if (VelocityMillimetresPerSecond is { } value && !double.IsFinite(value))
        {
            yield return new ValidationResult(
                "The reported velocity must be finite.",
                [nameof(VelocityMillimetresPerSecond)]);
        }
    }
}
