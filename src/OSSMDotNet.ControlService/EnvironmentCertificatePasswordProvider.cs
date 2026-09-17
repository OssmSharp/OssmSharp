namespace OSSMDotNet.ControlService;

/// <summary>Reads an optional HTTPS certificate password from the dedicated process environment variable.</summary>
public sealed class EnvironmentCertificatePasswordProvider : IControlServiceCertificatePasswordProvider
{
    /// <summary>Returns the optional password from OSSM_CONTROL_HTTPS_CERTIFICATE_PASSWORD.</summary>
    public string? GetPassword() => Environment.GetEnvironmentVariable("OSSM_CONTROL_HTTPS_CERTIFICATE_PASSWORD");
}
