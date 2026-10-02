using System.ComponentModel.DataAnnotations;

namespace OssmSharp.Web.ServiceDefaults.Models;

/// <summary>
/// Configures the guest web listener.
/// </summary>
public sealed class GuestOptions : IValidatableObject
{
    /// <summary>
    /// Gets or sets the guest listener port, from 1024 through 65535.
    /// </summary>
    [Range(1024, 65535)]
    public int GuestPort { get; set; }

    /// <summary>
    /// Gets or sets the guest app's public HTTPS URL, without a path.
    /// </summary>
    /// <remarks>
    /// Leave empty for local access only. The reverse proxy must run on the same computer.
    /// Restart the app after changing this value.
    /// </remarks>
    public string Hostname { get; set; } = string.Empty;

    /// <inheritdoc />
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Hostname.Length != 0
            && (!Uri.TryCreate(Hostname, UriKind.Absolute, out var origin)
                || !origin.IsWellFormedOriginalString()
                || origin.Scheme != "https"
                || origin.UserInfo.Length != 0
                || origin.AbsolutePath != "/"
                || origin.Query.Length != 0
                || origin.Fragment.Length != 0
                || origin.HostNameType is UriHostNameType.Unknown
                || origin.Port is < 1 or > 65535))
            yield return new("Hostname must be a path-free HTTPS origin without credentials, query, or fragment.", [nameof(Hostname)]);
    }
}
