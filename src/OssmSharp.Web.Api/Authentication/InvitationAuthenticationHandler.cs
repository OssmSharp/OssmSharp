using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using OssmSharp.Web.Api.Control;

namespace OssmSharp.Web.Api.Authentication;

/// <summary>
/// Authenticates guests without redeeming their invitation.
/// </summary>
/// <param name="options">The scheme options.</param>
/// <param name="logger">The authentication logger factory.</param>
/// <param name="encoder">The handler's URL encoder.</param>
/// <param name="session">The session that checks the invitation.</param>
public sealed class InvitationAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IControlSession session) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    /// <summary>
    /// Gets the authentication scheme for invitation tokens.
    /// </summary>
    public const string SchemeName = "Bearer";

    /// <inheritdoc />
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var authorization = Request.Headers.Authorization;
        if (authorization.Count != 1 || authorization[0] is not { } value
            || !value.StartsWith("Bearer ", StringComparison.Ordinal))
            return Task.FromResult(AuthenticateResult.Fail("Invalid invitation."));

        var invitationId = session.AuthenticateInvitation(value[7..]);
        if (invitationId is null)
            return Task.FromResult(AuthenticateResult.Fail("Invalid invitation."));

        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, invitationId), new Claim(ClaimTypes.Role, ControlServiceRoles.GuestWeb)],
            Scheme.Name, ClaimTypes.Name, ClaimTypes.Role);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
