using System.ComponentModel.DataAnnotations;

namespace OssmSharp.Runtime.Oem;

/// <summary>
/// Configures request retries and terminal-response waits for a device.
/// </summary>
public sealed class OssmDeviceOptions : IValidatableObject
{
    /// <summary>
    /// Gets or sets the maximum transmissions per request, including the initial attempt. Defaults to three.
    /// </summary>
    public int MaximumRequestAttempts { get; set; } = 3;

    /// <summary>
    /// Gets or sets the time allowed for a terminal response after each request transfer settles. Defaults to two seconds.
    /// </summary>
    public TimeSpan ResponseTimeout { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Validates the retry limit and supported response timeout duration.
    /// </summary>
    /// <param name="validationContext">The context for validating these options.</param>
    /// <returns>A validation result naming each invalid property.</returns>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (MaximumRequestAttempts < 1)
        {
            yield return new("At least one request transmission is required.", [nameof(MaximumRequestAttempts)]);
        }

        if (ResponseTimeout <= TimeSpan.Zero || ResponseTimeout > TimeSpan.FromMilliseconds(uint.MaxValue - 1))
        {
            yield return new("The response timeout must be positive and within the supported timer duration.", [nameof(ResponseTimeout)]);
        }
    }
}
