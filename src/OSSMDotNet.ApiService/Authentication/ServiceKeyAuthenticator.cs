using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using OSSMDotNet.ControlService.Models;
using OSSMDotNet.ServiceDefaults;

namespace OSSMDotNet.ApiService.Authentication;

/// <summary>
/// Validates configured service credentials without retaining their plaintext values.
/// </summary>
public sealed class ServiceKeyAuthenticator : IDisposable
{
    private byte[] _adminWebKeyHash;
    private byte[] _guestWebKeyHash;

    /// <summary>
    /// Creates a validator from startup-validated service-key configuration.
    /// </summary>
    /// <param name="options">The distinct admin and guest service keys to hash.</param>
    /// <exception cref="ArgumentNullException">The configuration is null.</exception>
    public ServiceKeyAuthenticator(IOptions<ControlServiceOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _adminWebKeyHash = CapabilityTokens.Hash(options.Value.AdminWebServiceKey);
        _guestWebKeyHash = CapabilityTokens.Hash(options.Value.GuestWebServiceKey);
    }

    /// <summary>
    /// Identifies a service from exactly one authorization header with exactly one matching key.
    /// </summary>
    /// <param name="authorization">The incoming Authorization header values.</param>
    /// <returns>The matching service identity, or null for missing, ambiguous, or invalid credentials.</returns>
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

    /// <summary>
    /// Clears retained service-key digests during application shutdown.
    /// </summary>
    public void Dispose()
    {
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(_adminWebKeyHash);
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(_guestWebKeyHash);
        _adminWebKeyHash = [];
        _guestWebKeyHash = [];
    }
}
