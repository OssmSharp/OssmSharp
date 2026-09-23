namespace OSSMDotNet.Api.Hosting;

/// <summary>
/// Retrieves the HTTPS certificate password without binding it from ordinary application configuration.
/// </summary>
public interface IControlServiceCertificatePasswordProvider
{
    /// <summary>
    /// Retrieves the password for the configured HTTPS certificate, if one was supplied securely.
    /// </summary>
    /// <returns>The certificate password, or null when none is configured.</returns>
    string? GetPassword();
}
