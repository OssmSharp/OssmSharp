using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using OssmSharp.Web.Api.Control;

namespace OssmSharp.Web.Api.Authentication;

/// <summary>
/// Authenticates saved guest sessions against the current invitation and absolute expiry.
/// </summary>
/// <param name="options">Authentication scheme options.</param>
/// <param name="logger">The authentication logger factory.</param>
/// <param name="encoder">The framework URL encoder.</param>
/// <param name="session">The API-owned guest session.</param>
public sealed class GuestSessionAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IControlSession session) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    /// <summary>
    /// Gets the authentication scheme used for saved guest credentials.
    /// </summary>
    public const string SchemeName = "GuestSession";

    /// <inheritdoc />
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var authorization = Request.Headers.Authorization;
        if (authorization.Count != 1 || authorization[0] is not { } value
            || !value.StartsWith("Session ", StringComparison.Ordinal))
            return Task.FromResult(AuthenticateResult.Fail("Invalid guest session."));

        var identity = session.AuthenticateGuestSession(value[8..]);
        if (identity is null)
            return Task.FromResult(AuthenticateResult.Fail("Invalid guest session."));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, identity), new Claim(ClaimTypes.Role, ControlServiceRoles.GuestWeb)],
            Scheme.Name,
            ClaimTypes.Name,
            ClaimTypes.Role));
        return Task.FromResult(AuthenticateResult.Success(new(principal, Scheme.Name)));
    }
}
