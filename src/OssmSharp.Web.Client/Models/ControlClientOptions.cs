using System.ComponentModel.DataAnnotations;

namespace OssmSharp.Web.Client.Models;

/// <summary>Configures one authenticated server-side connection to the versioned control hub.</summary>
public sealed class ControlClientOptions : IValidatableObject
{
    /// <summary>
    /// Gets or sets the path-free origin of the trusted control service.
    /// </summary>
    [Required]
    public string ControlOrigin { get; set; } = "";

    /// <summary>
    /// Gets or sets the service-to-service authentication key; never expose it to browsers.
    /// </summary>
    [Required, RegularExpression(@"\A[A-Za-z0-9_-]{42}[AEIMQUYcgkosw048]\z")]
    public string ServiceKey { get; set; } = "";

    /// <summary>
    /// Gets or sets the connection keep-alive interval in seconds.
    /// </summary>
    [Range(1, 300)]
    public int KeepAliveSeconds { get; set; } = 1;

    /// <summary>
    /// Gets or sets the server inactivity timeout in seconds.
    /// </summary>
    [Range(2, 600)]
    public int ServerTimeoutSeconds { get; set; } = 3;

    /// <summary>
    /// Gets or sets the command response timeout in seconds, excluding device connection setup.
    /// </summary>
    [Range(1, 30)]
    public int CommandTimeoutSeconds { get; set; } = 2;

    /// <summary>Validates the trusted hub origin and compatible elapsed-time connection settings.</summary>
    /// <param name="validationContext">The validation context supplied by the options system.</param>
    /// <returns>Validation failures for the origin or timing settings.</returns>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!Uri.TryCreate(ControlOrigin, UriKind.Absolute, out var origin) ||
            origin.UserInfo.Length != 0 || origin.AbsolutePath != "/" ||
            origin.Query.Length != 0 || origin.Fragment.Length != 0 ||
            (origin.IsLoopback ? origin.Scheme != "http" : origin.Scheme != "https") ||
            (!origin.IsLoopback && origin.HostNameType == UriHostNameType.Unknown) ||
            origin.Port is < 1 or > 65535)
        {
            yield return new(
                "ControlOrigin must be a path-free loopback HTTP origin or a non-loopback HTTPS origin.",
                [nameof(ControlOrigin)]);
        }

        if (ServerTimeoutSeconds < KeepAliveSeconds * 2)
            yield return new(
                "ServerTimeoutSeconds must be at least twice KeepAliveSeconds.",
                [nameof(ServerTimeoutSeconds)]);
    }
}
