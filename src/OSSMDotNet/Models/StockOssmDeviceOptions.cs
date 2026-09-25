using System.ComponentModel.DataAnnotations;

namespace OSSMDotNet.Models;

/// <summary>
/// Provides shared options for OSSM device implementations.
/// </summary>
public record StockOssmDeviceOptions : IValidatableObject
{
    /// <summary>
    /// Gets or sets the maximum interval in which reported feedback can match a pending motion write.
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
