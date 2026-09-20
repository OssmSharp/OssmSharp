using System.ComponentModel.DataAnnotations;

namespace OSSMDotNet;

/// <summary>
/// Provides shared timing options for OSSM device implementations.
/// </summary>
public abstract class OssmDeviceBaseOptions : IValidatableObject
{
    /// <summary>
    /// Gets or sets the interval during which motion telemetry is live.
    /// </summary>
    public TimeSpan ObservationLiveFor { get; set; } = TimeSpan.FromMilliseconds(1500);

    /// <summary>
    /// Gets or sets the maximum interval for matching feedback to a write.
    /// </summary>
    public TimeSpan ExpectationLifetime { get; set; } = TimeSpan.FromMilliseconds(1500);

    /// <inheritdoc />
    public virtual IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (ObservationLiveFor <= TimeSpan.Zero)
        {
            yield return new ValidationResult(
                "ObservationLiveFor must be positive.",
                [nameof(ObservationLiveFor)]);
        }

        if (ExpectationLifetime <= TimeSpan.Zero)
        {
            yield return new ValidationResult(
                "ExpectationLifetime must be positive.",
                [nameof(ExpectationLifetime)]);
        }
    }
}
