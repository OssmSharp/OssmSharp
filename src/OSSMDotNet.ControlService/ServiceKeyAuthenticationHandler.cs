using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace OSSMDotNet.ControlService;

/// <summary>Authenticates a trusted web service from one configured Authorization header credential.</summary>
public sealed class ServiceKeyAuthenticationHandler(
    IOptionsMonitor<ServiceKeyAuthenticationOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    ServiceKeyAuthenticator authenticator)
    : AuthenticationHandler<ServiceKeyAuthenticationOptions>(options, logger, encoder)
{
    /// <inheritdoc />
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var identity = authenticator.Authenticate(Request.Headers.Authorization);
        if (identity is null)
            return Task.FromResult(AuthenticateResult.Fail("Invalid service credentials."));

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, identity.Name),
            new Claim(ClaimTypes.Name, identity.Name),
            new Claim(ClaimTypes.Role, identity.Role)
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name, ClaimTypes.Name, ClaimTypes.Role));
        var ticket = new AuthenticationTicket(principal, Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
