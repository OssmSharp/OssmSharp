using System.ComponentModel.DataAnnotations;

namespace OSSMDotNet;

/// <summary>
/// Provides shared options for OSSM device implementations.
/// </summary>
public abstract class OssmDeviceBaseOptions : IValidatableObject
{
    /// <summary>
    /// Gets or sets the maximum interval for matching feedback to a write.
    /// TODO: what is this? bad name.
    /// </summary>
    public TimeSpan ExpectationLifetime { get; set; } = TimeSpan.FromMilliseconds(1500);

    /// <inheritdoc />
    public virtual IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (ExpectationLifetime <= TimeSpan.Zero)
        {
            yield return new ValidationResult(
                "ExpectationLifetime must be positive.",
                [nameof(ExpectationLifetime)]);
        }
    }
}
