namespace OSSMDotNet.Api.Hosting;

/// <summary>
/// Reads an optional HTTPS certificate password from the dedicated process environment variable.
/// </summary>
public sealed class EnvironmentCertificatePasswordProvider : IControlServiceCertificatePasswordProvider
{
    /// <inheritdoc />
    public string? GetPassword() => Environment.GetEnvironmentVariable("OSSM_CONTROL_HTTPS_CERTIFICATE_PASSWORD");
}
