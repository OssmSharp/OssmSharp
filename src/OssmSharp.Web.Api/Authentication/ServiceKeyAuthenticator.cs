using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using OssmSharp.Web.Api.Models;

namespace OssmSharp.Web.Api.Authentication;

/// <summary>
/// Validates configured service credentials without retaining their plaintext values.
/// </summary>
public sealed class ServiceKeyAuthenticator : IDisposable
{
    private byte[] _adminWebKeyHash;

    /// <summary>
    /// Creates a validator from startup-validated service-key configuration.
    /// </summary>
    /// <param name="options">The administrator service key settings.</param>
    /// <exception cref="ArgumentNullException">The configuration is null.</exception>
    public ServiceKeyAuthenticator(IOptions<ControlServiceOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _adminWebKeyHash = CapabilityTokens.Hash(options.Value.AdminWebServiceKey);
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
        return CapabilityTokens.Matches(_adminWebKeyHash, key)
            ? new("admin-web", ControlServiceRoles.AdminWeb)
            : null;
    }

    /// <summary>
    /// Clears retained service-key digests during application shutdown.
    /// </summary>
    public void Dispose()
    {
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(_adminWebKeyHash);
        _adminWebKeyHash = [];
    }
}
