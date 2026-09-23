using System.ComponentModel.DataAnnotations;
using System.Net;

namespace OSSMDotNet.ControlService.Models;

/// <summary>Configures the private control-service listener, simulator, and trusted service identities.</summary>
public sealed class ControlServiceOptions : IValidatableObject
{
    /// <summary>
    /// Gets or sets the IP address on which the control service listens.
    /// </summary>
    [Required]
    public string BindAddress { get; set; } = "127.0.0.1";

    /// <summary>
    /// Gets or sets the host name clients use to reach the listener.
    /// </summary>
    [Required]
    public string HostName { get; set; } = "localhost";

    /// <summary>
    /// Gets or sets the control service listener port.
    /// </summary>
    [Range(1024, 65535)]
    public int ControlPort { get; set; }

    /// <summary>
    /// Gets or sets the path-free origin published in guest invitations.
    /// </summary>
    [Required]
    public string GuestOrigin { get; set; } = "";

    /// <summary>
    /// Gets or sets the native simulator executable path.
    /// </summary>
    [Required]
    public string SimulatorPath { get; set; } = "";

    /// <summary>
    /// Gets or sets the certificate path required for a non-loopback listener.
    /// </summary>
    public string HttpsCertificatePath { get; set; } = "";

    /// <summary>
    /// Gets or sets the hub keep-alive interval in seconds.
    /// </summary>
    [Range(1, 300)]
    public int KeepAliveSeconds { get; set; } = 1;

    /// <summary>
    /// Gets or sets the hub client inactivity timeout in seconds.
    /// </summary>
    [Range(2, 600)]
    public int ClientTimeoutSeconds { get; set; } = 3;

    /// <summary>
    /// Gets or sets the admin server-to-server key, which must remain private.
    /// </summary>
    [Required, RegularExpression(@"\A[A-Za-z0-9_-]{42}[AEIMQUYcgkosw048]\z")]
    public string AdminWebServiceKey { get; set; } = "";

    /// <summary>
    /// Gets or sets the separate guest server-to-server key, which must remain private.
    /// </summary>
    [Required, RegularExpression(@"\A[A-Za-z0-9_-]{42}[AEIMQUYcgkosw048]\z")]
    public string GuestWebServiceKey { get; set; } = "";

    /// <summary>Validates direct-service endpoint, published guest origin, and SignalR liveness configuration.</summary>
    /// <param name="validationContext">The validation context supplied by the options system.</param>
    /// <returns>Validation failures for the configured service endpoint and liveness settings.</returns>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!IPAddress.TryParse(BindAddress, out var bindAddress))
            yield return new("BindAddress must be a valid IP address.", [nameof(BindAddress)]);
        else if (!IPAddress.IsLoopback(bindAddress) && HttpsCertificatePath.Length == 0)
            yield return new("HttpsCertificatePath is required for a non-loopback BindAddress.", [nameof(HttpsCertificatePath)]);

        if (HttpsCertificatePath.Length > 0 && !Path.IsPathFullyQualified(HttpsCertificatePath))
            yield return new("HttpsCertificatePath must be an absolute path.", [nameof(HttpsCertificatePath)]);

        if (bindAddress is not null && IsHostInvalid(bindAddress))
            yield return new("HostName must be a valid host for the configured bind scope.", [nameof(HostName)]);

        if (IsGuestOriginInvalid())
            yield return new("GuestOrigin must be a path-free loopback HTTP origin or a non-loopback HTTPS origin.", [nameof(GuestOrigin)]);

        if (string.Equals(AdminWebServiceKey, GuestWebServiceKey, StringComparison.Ordinal))
            yield return new("AdminWebServiceKey and GuestWebServiceKey must be distinct.", [nameof(AdminWebServiceKey), nameof(GuestWebServiceKey)]);

        if (ClientTimeoutSeconds < KeepAliveSeconds * 2)
            yield return new("ClientTimeoutSeconds must be at least twice KeepAliveSeconds.", [nameof(ClientTimeoutSeconds)]);
    }

    private bool IsHostInvalid(IPAddress bindAddress)
    {
        var host = HostName.Trim('[', ']');
        var isLoopbackHost = string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
            (IPAddress.TryParse(host, out var hostAddress) && IPAddress.IsLoopback(hostAddress));
        return Uri.CheckHostName(host) == UriHostNameType.Unknown ||
            (IPAddress.IsLoopback(bindAddress) && !isLoopbackHost) ||
            (!IPAddress.IsLoopback(bindAddress) && isLoopbackHost);
    }

    private bool IsGuestOriginInvalid()
    {
        if (!Uri.TryCreate(GuestOrigin, UriKind.Absolute, out var origin))
            return true;
        if (origin.UserInfo.Length != 0 || origin.AbsolutePath != "/" || origin.Query.Length != 0 || origin.Fragment.Length != 0)
            return true;
        if (origin.IsLoopback)
            return origin.Scheme != "http" || origin.Port == ControlPort;
        return origin.Scheme != "https" || origin.HostNameType == UriHostNameType.Unknown || origin.Port is < 1 or > 65535;
    }
}
