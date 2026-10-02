using System.ComponentModel.DataAnnotations;

namespace OssmSharp.Web.ServiceDefaults.Models;

/// <summary>
/// Configures the loopback-only admin web listener.
/// </summary>
public sealed class AdminOptions : IValidatableObject
{
    /// <summary>
    /// Gets or sets the loopback listener port, from 1024 through 65535.
    /// </summary>
    [Range(1024, 65535)]
    public int AdminPort { get; set; }

    /// <summary>
    /// Gets or sets the HTTPS guest origin used for invitations. HTTP is allowed only for loopback.
    /// </summary>
    [Required]
    [Url]
    public string GuestHostname { get; set; } = string.Empty;

    /// <inheritdoc />
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!Uri.TryCreate(GuestHostname, UriKind.Absolute, out var origin)
            || !origin.IsWellFormedOriginalString()
            || (origin.Scheme != "https" && !(origin.Scheme == "http" && origin.IsLoopback))
            || origin.UserInfo.Length != 0
            || origin.AbsolutePath != "/"
            || origin.Query.Length != 0
            || origin.Fragment.Length != 0
            || origin.HostNameType is UriHostNameType.Unknown
            || origin.Port is < 1 or > 65535)
            yield return new("GuestHostname must be an HTTPS origin without a path, credentials, query, or fragment. HTTP is allowed only for loopback.", [nameof(GuestHostname)]);
    }
}
