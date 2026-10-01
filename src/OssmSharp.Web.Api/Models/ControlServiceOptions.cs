using System.ComponentModel.DataAnnotations;

namespace OssmSharp.Web.Api.Models;

/// <summary>
/// Configures the selected device, UI session, and trusted service identities.
/// </summary>
public sealed class ControlServiceOptions : IValidatableObject
{
    /// <summary>
    /// Gets or sets the device identifier, or an empty value to discover the first available device on connection.
    /// </summary>
    public string DeviceIdentifier { get; set; } = string.Empty;

    /// <summary>Gets or sets the SignalR keep-alive interval in seconds.</summary>
    [Range(1, 300)]
    public int KeepAliveSeconds { get; set; } = 1;

    /// <summary>Gets or sets the SignalR client inactivity timeout in seconds.</summary>
    [Range(2, 600)]
    public int ClientTimeoutSeconds { get; set; } = 3;

    /// <summary>Gets or sets how often a required Stop is retried.</summary>
    [Range(50, 1000)]
    public int GuestControlCheckMilliseconds { get; set; } = 100;

    /// <summary>Gets or sets the user-facing server's private service key.</summary>
    [Required, RegularExpression(@"\A[A-Za-z0-9_-]{42}[AEIMQUYcgkosw048]\z")]
    public string AdminWebServiceKey { get; set; } = string.Empty;

    /// <summary>Gets or sets the guest-facing server's distinct private service key.</summary>
    [Required, RegularExpression(@"\A[A-Za-z0-9_-]{42}[AEIMQUYcgkosw048]\z")]
    public string GuestWebServiceKey { get; set; } = string.Empty;

    /// <inheritdoc />
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext) =>
        ValidateRelationships();

    internal IEnumerable<ValidationResult> ValidateRelationships()
    {
        if (string.Equals(AdminWebServiceKey, GuestWebServiceKey, StringComparison.Ordinal))
        {
            yield return new(
                "AdminWebServiceKey and GuestWebServiceKey must be distinct.",
                [nameof(AdminWebServiceKey), nameof(GuestWebServiceKey)]);
        }

        if (ClientTimeoutSeconds < KeepAliveSeconds * 2)
        {
            yield return new(
                "ClientTimeoutSeconds must be at least twice KeepAliveSeconds.",
                [nameof(ClientTimeoutSeconds)]);
        }
    }
}
