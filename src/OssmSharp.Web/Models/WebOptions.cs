using System.ComponentModel.DataAnnotations;

namespace OssmSharp.Web.Models;

/// <summary>
/// Configures the three loopback listeners owned by the single-process web host.
/// </summary>
public sealed record WebOptions : IValidatableObject
{
    /// <summary>
    /// Gets or sets the private control API port.
    /// </summary>
    [Range(1024, 65535)]
    public int ApiPort { get; set; } = 5003;

    /// <summary>
    /// Gets or sets the local user application port.
    /// </summary>
    [Range(1024, 65535)]
    public int AdminPort { get; set; } = 5002;

    /// <summary>
    /// Gets or sets the guest application port.
    /// </summary>
    [Range(1024, 65535)]
    public int GuestPort { get; set; } = 5001;

    /// <inheritdoc />
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (ApiPort == AdminPort || ApiPort == GuestPort || AdminPort == GuestPort)
            yield return new("API, user, and guest ports must be different.", [nameof(ApiPort), nameof(AdminPort), nameof(GuestPort)]);
    }
}
