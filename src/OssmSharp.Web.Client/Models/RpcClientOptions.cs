using System.ComponentModel.DataAnnotations;

namespace OssmSharp.Web.Client.Models;

/// <summary>
/// Configures one authenticated server-side connection to the versioned control hub.
/// </summary>
public sealed class RpcClientOptions : IValidatableObject
{
    /// <summary>
    /// Gets or sets the URI of the API.
    /// </summary>
    [Required]
    public string EndpointUri { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the administrator service key. Guest clients reject a configured key.
    /// </summary>
    [RegularExpression(@"\A[A-Za-z0-9_-]{42}[AEIMQUYcgkosw048]\z")]
    public string ServiceKey { get; set; } = string.Empty;

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
    /// Gets or sets the request timeout in seconds, excluding device connection setup.
    /// </summary>
    [Range(1, 30)]
    public int RequestTimeoutSeconds { get; set; } = 2;

    /// <summary>
    /// Validates the trusted hub origin and compatible elapsed-time connection settings.
    /// </summary>
    /// <param name="validationContext">The validation context supplied by the options system.</param>
    /// <returns>Validation failures for the origin or timing settings.</returns>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!Uri.TryCreate(EndpointUri, UriKind.Absolute, out var origin) ||
            origin.UserInfo.Length != 0 || origin.AbsolutePath != "/" ||
            origin.Query.Length != 0 || origin.Fragment.Length != 0 ||
            (origin.IsLoopback ? origin.Scheme != "http" : origin.Scheme != "https") ||
            (!origin.IsLoopback && origin.HostNameType == UriHostNameType.Unknown) ||
            origin.Port is < 1 or > 65535)
        {
            yield return new(
                "EndpointUri must be loopback or a non-loopback HTTP URI.",
                [nameof(EndpointUri)]);
        }

        if (ServerTimeoutSeconds < KeepAliveSeconds * 2)
            yield return new(
                "ServerTimeoutSeconds must be at least twice KeepAliveSeconds.",
                [nameof(ServerTimeoutSeconds)]);
    }
}
