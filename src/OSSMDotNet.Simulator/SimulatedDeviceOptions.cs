using System.ComponentModel.DataAnnotations;

namespace OSSMDotNet.Simulator;

/// <summary>
/// Configures the native OSSM-RS simulator device implementation.
/// </summary>
public sealed record SimulatedDeviceOptions : StockOssmDeviceOptions
{
    /// <summary>
    /// Gets or sets the absolute path to the <c>ossm-sim-bridge</c> executable.
    /// </summary>
    [Required]
    public string ExecutablePath { get; set; } = string.Empty;

    /// <inheritdoc />
    public override IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        foreach (var result in base.Validate(validationContext))
        {
            yield return result;
        }

        if (string.IsNullOrWhiteSpace(ExecutablePath))
        {
            yield return new ValidationResult(
                "ExecutablePath is required.",
                [nameof(ExecutablePath)]);
        }
        else if (!Path.IsPathFullyQualified(ExecutablePath))
        {
            yield return new ValidationResult(
                "ExecutablePath must be absolute.",
                [nameof(ExecutablePath)]);
        }
        else if (!string.IsNullOrWhiteSpace(ExecutablePath) && !File.Exists(ExecutablePath))
        {
            yield return new ValidationResult(
                "Build the ossm-sim-bridge executable before starting the simulator.",
                [nameof(ExecutablePath)]);
        }
    }
}
