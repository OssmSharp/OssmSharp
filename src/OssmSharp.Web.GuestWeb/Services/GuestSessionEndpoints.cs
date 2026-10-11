using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;

namespace OssmSharp.Web.GuestWeb.Services;

internal static class GuestSessionEndpoints
{
    internal const string Scheme = "GuestBrowser";
    internal const string CredentialClaim = "ossm:guest-session";
    internal const string CookieName = "ossm-guest-session";

    internal static void MapGuestSession(this WebApplication app)
    {
        app.MapGet("/guest-session", BootstrapAsync);
        app.MapPost("/guest-session/redeem", RedeemAsync);
        app.MapPost("/guest-session/leave", LeaveAsync);
        app.MapPost("/guest-session/clear", ClearAsync);
    }

    private static async Task<IResult> BootstrapAsync(
        HttpContext context, IGuestApiClient client, IAntiforgery antiforgery, ILoggerFactory logs)
    {
        var credential = context.User.FindFirstValue(CredentialClaim);
        if (credential is null)
        {
            if (context.Request.Cookies.ContainsKey(CookieName))
                await context.SignOutAsync(Scheme);
            return Results.Ok(new { code = "no-session", requestToken = antiforgery.GetAndStoreTokens(context).RequestToken });
        }
        try
        {
            var session = await client.GetSavedSessionAsync(credential, context.RequestAborted);
            return Results.Ok(new
            {
                code = "ready", requestToken = antiforgery.GetAndStoreTokens(context).RequestToken, connected = session.Connected,
                canAttach = session.CanAttach, expiresAt = session.ExpiresAt,
            });
        }
        catch (ApiClientException error) when (error.Code == ControlResponseCodes.InvalidGuestSession)
        {
            await context.SignOutAsync(Scheme);
            context.User = new ClaimsPrincipal(new ClaimsIdentity());
            logs.CreateLogger("OssmSharp.Web.GuestWeb.Services.GuestSession").SessionRequestRejected("bootstrap", error.Code);
            return Results.Json(new { code = error.Code, requestToken = antiforgery.GetAndStoreTokens(context).RequestToken }, statusCode: StatusCodes.Status401Unauthorized);
        }
        catch (Exception error) when (error is RpcClientRequestTimeoutException
            or ApiClientException { Code: "connection-unavailable" })
        {
            logs.CreateLogger("OssmSharp.Web.GuestWeb.Services.GuestSession").SessionTransportUnavailable("bootstrap");
            return Results.Json(new { code = "connection-unavailable", requestToken = antiforgery.GetAndStoreTokens(context).RequestToken }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (Exception error) when (error is InvalidDataException or ApiClientException)
        {
            logs.CreateLogger("OssmSharp.Web.GuestWeb.Services.GuestSession").SessionResponseInvalid("bootstrap");
            return Results.Json(new { code = "invalid-response", requestToken = antiforgery.GetAndStoreTokens(context).RequestToken }, statusCode: StatusCodes.Status502BadGateway);
        }
    }

    private static async Task<IResult> RedeemAsync(
        HttpContext context,
        GuestInvitation invitation,
        IGuestApiClient client,
        IAntiforgery antiforgery,
        ILoggerFactory logs,
        TimeProvider time)
    {
        var logger = logs.CreateLogger("OssmSharp.Web.GuestWeb.Services.GuestSession");
        if (!await ValidateAntiforgeryAsync(context, antiforgery, logger))
            return Results.BadRequest(new { code = "invalid-request" });
        if (context.User.Identity?.IsAuthenticated == true)
        {
            logger.SessionRequestRejected("redeem", "session-exists");
            return Results.Conflict(new { code = "session-exists" });
        }
        if (invitation.Token is not { Length: > 0 and <= 128 }
            || invitation.Token.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_')))
        {
            logger.SessionRequestRejected("redeem", ControlResponseCodes.InvalidInvitation);
            return Results.BadRequest(new { code = ControlResponseCodes.InvalidInvitation });
        }

        try
        {
            var redeemed = await client.RedeemAsync(invitation.Token, context.RequestAborted);
            if (redeemed.SessionToken is null || redeemed.ExpiresAt is not { } expiry || expiry <= time.GetUtcNow())
                throw new InvalidDataException("The API returned an invalid saved-session lifetime.");
            var principal = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, redeemed.Correlation), new Claim(CredentialClaim, redeemed.SessionToken)], Scheme));
            await context.SignInAsync(Scheme, principal, new()
            {
                IsPersistent = true,
                AllowRefresh = false,
                IssuedUtc = expiry.AddHours(-24),
                ExpiresUtc = expiry,
            });
            logger.BrowserSessionSaved();
            return Results.Ok(new { code = "saved" });
        }
        catch (ApiClientException error) when (error.Code is ControlResponseCodes.InvalidInvitation or ControlResponseCodes.GuestAlreadyJoined)
        {
            logger.SessionRequestRejected("redeem", error.Code);
            return Results.Json(new { code = error.Code }, statusCode: StatusCodes.Status409Conflict);
        }
        catch (Exception error) when (error is ApiClientException or RpcClientRequestTimeoutException)
        {
            // Redemption is not retryable: a lost response can follow successful consumption.
            logger.SessionTransportUnavailable("redeem");
            return Results.Json(new { code = "redemption-unknown" }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (InvalidDataException)
        {
            logger.SessionResponseInvalid("redeem");
            return Results.Json(new { code = "invalid-response" }, statusCode: StatusCodes.Status502BadGateway);
        }
    }

    private static async Task<IResult> LeaveAsync(
        HttpContext context, IGuestApiClient client, IAntiforgery antiforgery, ILoggerFactory logs)
    {
        var logger = logs.CreateLogger("OssmSharp.Web.GuestWeb.Services.GuestSession");
        if (!await ValidateAntiforgeryAsync(context, antiforgery, logger))
            return Results.BadRequest(new { accepted = false, code = "invalid-request" });
        var credential = context.User.FindFirstValue(CredentialClaim);
        if (credential is null)
        {
            logger.SessionRequestRejected("leave", "no-session");
            return Results.BadRequest(new { accepted = false, code = "no-session" });
        }
        var retainCookie = false;
        try
        {
            var result = await client.LeaveSavedSessionAsync(credential, context.RequestAborted);
            logger.BrowserSessionEnded();
            return Results.Ok(new { accepted = result.Accepted, code = result.Code });
        }
        catch (ApiClientException error) when (error.Code == ControlResponseCodes.GuestAlreadyJoined)
        {
            retainCookie = true;
            logger.SessionRequestRejected("leave", error.Code);
            return Results.Json(new { accepted = false, code = error.Code }, statusCode: StatusCodes.Status409Conflict);
        }
        catch (ApiClientException error) when (error.Code == "invalid-response")
        {
            logger.SessionResponseInvalid("leave");
            return Results.Json(new { accepted = false, code = error.Code }, statusCode: StatusCodes.Status502BadGateway);
        }
        catch (ApiClientException error)
        {
            logger.SessionRequestRejected("leave", error.Code);
            return Results.Json(new { accepted = false, code = error.Code }, statusCode: StatusCodes.Status409Conflict);
        }
        catch (RpcClientRequestTimeoutException)
        {
            logger.SessionTransportUnavailable("leave");
            return Results.Json(new { accepted = false, code = "outcome-unknown" }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        finally
        {
            if (!retainCookie)
                await context.SignOutAsync(Scheme);
        }
    }

    private static async Task<IResult> ClearAsync(HttpContext context, IAntiforgery antiforgery, ILoggerFactory logs)
    {
        var logger = logs.CreateLogger("OssmSharp.Web.GuestWeb.Services.GuestSession");
        if (!await ValidateAntiforgeryAsync(context, antiforgery, logger))
            return Results.BadRequest(new { code = "invalid-request" });
        await context.SignOutAsync(Scheme);
        logger.BrowserCookieCleared();
        return Results.Ok(new { code = "cookie-cleared" });
    }

    private static async Task<bool> ValidateAntiforgeryAsync(HttpContext context, IAntiforgery antiforgery, ILogger logger)
    {
        try
        {
            await antiforgery.ValidateRequestAsync(context);
            return true;
        }
        catch (AntiforgeryValidationException)
        {
            logger.SessionRequestRejected("antiforgery", "invalid-request");
            return false;
        }
    }
}
