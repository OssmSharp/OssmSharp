namespace OSSMDotNet.ControlService;

/// <summary>Retrieves the HTTPS certificate password without binding it from ordinary application configuration.</summary>
public interface IControlServiceCertificatePasswordProvider
{
    /// <summary>Returns the password for the configured HTTPS certificate, if one was supplied securely.</summary>
    string? GetPassword();
}
