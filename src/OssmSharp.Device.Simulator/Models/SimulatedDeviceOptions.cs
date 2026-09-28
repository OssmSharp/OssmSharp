using System.ComponentModel.DataAnnotations;

namespace OssmSharp.Device.Simulator.Models;

/// <summary>
/// Configures the native OSSM-RS simulator device implementation.
/// </summary>
public sealed record SimulatedDeviceOptions : IValidatableObject
{
    /// <summary>
    /// Gets or sets the endpoint of an already-running loopback simulator.
    /// </summary>
    [Required]
    public string Endpoint { get; set; } = string.Empty;

    /// <inheritdoc />
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!Uri.TryCreate(Endpoint, UriKind.Absolute, out var endpoint)
            || endpoint.Port is < 1 or > 65535
            || !string.Equals(Endpoint, $"tcp://127.0.0.1:{endpoint.Port}", StringComparison.Ordinal))
        {
            yield return new ValidationResult(
                "Endpoint must be tcp://127.0.0.1:<port> with a port from 1 to 65535.",
                [nameof(Endpoint)]);
        }
    }
}
