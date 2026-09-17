using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using OSSMDotNet.ControlService.Models;
using OSSMDotNet.ServiceDefaults;

namespace OSSMDotNet.ControlService;

/// <summary>Validates configured service credentials without retaining their plaintext values.</summary>
public sealed class ServiceKeyAuthenticator : IDisposable
{
    private byte[] _adminWebKeyHash;
    private byte[] _guestWebKeyHash;

    /// <summary>Creates a validator from startup-validated service-key configuration.</summary>
    public ServiceKeyAuthenticator(IOptions<ControlServiceOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _adminWebKeyHash = CapabilityTokens.Hash(options.Value.AdminWebServiceKey);
        _guestWebKeyHash = CapabilityTokens.Hash(options.Value.GuestWebServiceKey);
    }

    /// <summary>Returns the distinct authenticated service identity for one unambiguous authorization header.</summary>
    public ServiceIdentity? Authenticate(StringValues authorization)
    {
        if (authorization.Count != 1)
            return null;

        var value = authorization[0];
        var prefix = ServiceKeyAuthenticationDefaults.Scheme + " ";
        if (value is null || !value.StartsWith(prefix, StringComparison.Ordinal))
            return null;

        var key = value[prefix.Length..];
        var adminMatches = CapabilityTokens.Matches(_adminWebKeyHash, key);
        var guestMatches = CapabilityTokens.Matches(_guestWebKeyHash, key);
        return (adminMatches, guestMatches) switch
        {
            (true, false) => new("admin-web", ControlServiceRoles.AdminWeb),
            (false, true) => new("guest-web", ControlServiceRoles.GuestWeb),
            _ => null
        };
    }

    /// <summary>Clears retained service-key digests during application shutdown.</summary>
    public void Dispose()
    {
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(_adminWebKeyHash);
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(_guestWebKeyHash);
        _adminWebKeyHash = [];
        _guestWebKeyHash = [];
    }
}
