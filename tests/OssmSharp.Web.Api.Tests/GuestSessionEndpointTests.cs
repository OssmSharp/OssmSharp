using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using FastMoq;
using FastMoq.Extensions;
using FastMoq.Providers;
using FastMoq.Providers.MoqProvider;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using OssmSharp.Web.Client.Clients;
using OssmSharp.Web.Client.Models;
using OssmSharp.Web.Contracts;
using OssmSharp.Web.GuestWeb;
using OssmSharp.Web.GuestWeb.Services;
using OssmSharp.Web.ServiceDefaults;
using Xunit;

namespace OssmSharp.Web.Api.Tests;

public sealed class GuestSessionEndpointTests : IAsyncDisposable
{
    private const string Invitation = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string Credential = "CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC";
    private const string LocalOrigin = "http://127.0.0.1:5001";
    private readonly Mocker _mocks = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero));
    private readonly CookieContainer _cookies = new();
    private WebApplication? _app;
    private Mock<IGuestApiClient>? _client;

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_app is not null)
                await _app.DisposeAsync();
        }
        finally
        {
            _mocks.Dispose();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "Integration")]
    public async Task RedemptionSetsProtectedHostOnlyHttpOnlyCookieWithFixedExpiry(bool publicOrigin)
    {
        using var http = await StartAsync(publicOrigin);
        var state = await BootstrapAsync(http);
        using var request = Mutation("/guest-session/redeem", state.GetProperty("requestToken").GetString()!, publicOrigin);
        request.Content = JsonContent.Create(new GuestInvitation(Invitation));

        using var response = await http.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var header = response.Headers.GetValues("Set-Cookie").Single(value => value.StartsWith(GuestSessionEndpoints.CookieName, StringComparison.Ordinal));
        Assert.Contains("httponly", header, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", header, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/", header, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("expires=Wed, 02 Jan 2030 00:00:00 GMT", header, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("domain=", header, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(publicOrigin, header.Contains("; secure", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(Credential, header, StringComparison.Ordinal);
        Assert.DoesNotContain(Credential, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        if (!publicOrigin)
        {
            var cookie = _cookies.GetCookies(http.BaseAddress!)[GuestSessionEndpoints.CookieName]!;
            var options = _app!.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(GuestSessionEndpoints.Scheme);
            var ticket = options.TicketDataFormat.Unprotect(cookie.Value)!;
            Assert.Equal(Credential, ticket.Principal.FindFirstValue(GuestSessionEndpoints.CredentialClaim));
            Assert.Equal(_clock.GetUtcNow(), ticket.Properties.IssuedUtc);
            Assert.Equal(_clock.GetUtcNow().AddHours(24), ticket.Properties.ExpiresUtc);
            Assert.True(ticket.Properties.IsPersistent);
            Assert.False(ticket.Properties.AllowRefresh);
        }
        _mocks.Verify<IGuestApiClient>(client => client.ResumeAsync(FastArg.Any<string>(), FastArg.Any<CancellationToken>()), TimesSpec.NeverCalled);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task CookieSurvivesNewClientWithoutExtendingLifetimeAndExpiresAtTheBoundary()
    {
        using var first = await StartAsync();
        await RedeemAsync(first);
        using var returning = CreateClient();
        _clock.Advance(TimeSpan.FromHours(12));

        var state = await BootstrapAsync(returning);

        Assert.Equal("ready", state.GetProperty("code").GetString());
        Assert.Equal(new DateTimeOffset(2030, 1, 2, 0, 0, 0, TimeSpan.Zero), state.GetProperty("expiresAt").GetDateTimeOffset());
        _clock.Advance(TimeSpan.FromHours(12));
        state = await BootstrapAsync(returning);
        Assert.Equal("no-session", state.GetProperty("code").GetString());
        Assert.Null(_cookies.GetCookies(returning.BaseAddress!)[GuestSessionEndpoints.CookieName]);
        _mocks.Verify<IGuestApiClient>(client => client.GetSavedSessionAsync(Credential, FastArg.Any<CancellationToken>()), TimesSpec.Once);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RevokedCookieIsClearedAndFreshInvitationCanBeRedeemedWithNewAntiforgeryToken()
    {
        using var http = await StartAsync();
        await RedeemAsync(http);
        _client!.Setup(client => client.GetSavedSessionAsync(Credential, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ApiClientException(ControlResponseCodes.InvalidGuestSession));

        var state = await BootstrapAsync(http);

        Assert.Equal(ControlResponseCodes.InvalidGuestSession, state.GetProperty("code").GetString());
        Assert.Null(_cookies.GetCookies(http.BaseAddress!)[GuestSessionEndpoints.CookieName]);
        using var request = Mutation("/guest-session/redeem", state.GetProperty("requestToken").GetString()!);
        request.Content = JsonContent.Create(new GuestInvitation(Invitation));
        using var response = await http.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        _mocks.Verify<IGuestApiClient>(client => client.RedeemAsync(Invitation, FastArg.Any<CancellationToken>()), TimesSpec.Exactly(2));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "Integration")]
    public async Task MissingAntiforgeryOrWrongOriginCannotConsumeAnInvitation(bool wrongOrigin)
    {
        using var http = await StartAsync();
        var state = await BootstrapAsync(http);
        using var request = Mutation("/guest-session/redeem", wrongOrigin ? state.GetProperty("requestToken").GetString()! : "invalid");
        if (wrongOrigin)
        {
            request.Headers.Remove("Origin");
            request.Headers.Add("Origin", "https://untrusted.example");
        }
        request.Content = JsonContent.Create(new GuestInvitation(Invitation));

        using var response = await http.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(wrongOrigin ? HttpStatusCode.Forbidden : HttpStatusCode.BadRequest, response.StatusCode);
        _mocks.Verify<IGuestApiClient>(client => client.RedeemAsync(FastArg.Any<string>(), FastArg.Any<CancellationToken>()), TimesSpec.NeverCalled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "Integration")]
    public async Task LeavingUsesSavedIdentityAndClearsCookieEvenWhenStopFails(bool stopFails)
    {
        using var http = await StartAsync();
        await RedeemAsync(http);
        var state = await BootstrapAsync(http);
        if (stopFails)
            _client!.Setup(client => client.LeaveSavedSessionAsync(Credential, It.IsAny<CancellationToken>()))
                .ThrowsAsync(new ApiClientException(ControlResponseCodes.DeviceIoFailed));
        using var request = Mutation("/guest-session/leave", state.GetProperty("requestToken").GetString()!);

        using var response = await http.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(stopFails ? HttpStatusCode.Conflict : HttpStatusCode.OK, response.StatusCode);
        Assert.Null(_cookies.GetCookies(http.BaseAddress!)[GuestSessionEndpoints.CookieName]);
        _mocks.Verify<IGuestApiClient>(client => client.LeaveSavedSessionAsync(Credential, FastArg.Any<CancellationToken>()), TimesSpec.Once);
        _mocks.Verify<IGuestApiClient>(client => client.ResumeAsync(FastArg.Any<string>(), FastArg.Any<CancellationToken>()), TimesSpec.NeverCalled);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task OfflineLeaveRejectionRetainsTheActiveControllersCookie()
    {
        using var http = await StartAsync();
        await RedeemAsync(http);
        var cookie = _cookies.GetCookies(http.BaseAddress!)[GuestSessionEndpoints.CookieName]!.Value;
        var state = await BootstrapAsync(http);
        _client!.Setup(client => client.LeaveSavedSessionAsync(Credential, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ApiClientException(ControlResponseCodes.GuestAlreadyJoined));
        using var request = Mutation("/guest-session/leave", state.GetProperty("requestToken").GetString()!);

        using var response = await http.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(cookie, _cookies.GetCookies(http.BaseAddress!)[GuestSessionEndpoints.CookieName]!.Value);
        _mocks.Verify<IGuestApiClient>(client => client.LeaveSavedSessionAsync(Credential, FastArg.Any<CancellationToken>()), TimesSpec.Once);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "Integration")]
    public async Task ClearingBrowserCookieRequiresAntiforgeryAndDoesNotReplayLeave(bool validProof)
    {
        using var http = await StartAsync();
        await RedeemAsync(http);
        var state = await BootstrapAsync(http);
        using var request = Mutation("/guest-session/clear", validProof ? state.GetProperty("requestToken").GetString()! : "invalid");

        using var response = await http.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(validProof ? HttpStatusCode.OK : HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(!validProof, _cookies.GetCookies(http.BaseAddress!)[GuestSessionEndpoints.CookieName] is not null);
        _mocks.Verify<IGuestApiClient>(client => client.LeaveSavedSessionAsync(FastArg.Any<string>(), FastArg.Any<CancellationToken>()), TimesSpec.NeverCalled);
        _mocks.Verify<IGuestApiClient>(client => client.LeaveAsync(FastArg.Any<CancellationToken>()), TimesSpec.NeverCalled);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ApiUnavailabilityPreservesTheExistingBrowserCookie()
    {
        using var http = await StartAsync();
        await RedeemAsync(http);
        var cookie = _cookies.GetCookies(http.BaseAddress!)[GuestSessionEndpoints.CookieName]!.Value;
        _client!.Setup(client => client.GetSavedSessionAsync(Credential, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ApiClientException("connection-unavailable"));

        using var response = await http.GetAsync("/guest-session", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var state = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal("connection-unavailable", state.GetProperty("code").GetString());
        Assert.Equal(cookie, _cookies.GetCookies(http.BaseAddress!)[GuestSessionEndpoints.CookieName]!.Value);
        Assert.DoesNotContain(Credential, state.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RejectedMetadataIsNotClassifiedAsATemporaryTransportFailure()
    {
        using var http = await StartAsync();
        await RedeemAsync(http);
        var cookie = _cookies.GetCookies(http.BaseAddress!)[GuestSessionEndpoints.CookieName]!.Value;
        _client!.Setup(client => client.GetSavedSessionAsync(Credential, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ApiClientException("request-failed"));

        using var response = await http.GetAsync("/guest-session", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var state = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal("invalid-response", state.GetProperty("code").GetString());
        Assert.NotNull(state.GetProperty("requestToken").GetString());
        Assert.Equal(cookie, _cookies.GetCookies(http.BaseAddress!)[GuestSessionEndpoints.CookieName]!.Value);
        _mocks.Verify<IGuestApiClient>(client => client.ResumeAsync(FastArg.Any<string>(), FastArg.Any<CancellationToken>()), TimesSpec.NeverCalled);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task TamperedCookieIsRemovedWithoutTryingToAttach()
    {
        using var http = await StartAsync();
        _cookies.Add(http.BaseAddress!, new Cookie(GuestSessionEndpoints.CookieName, "tampered", "/"));

        var state = await BootstrapAsync(http);

        Assert.Equal("no-session", state.GetProperty("code").GetString());
        Assert.Null(_cookies.GetCookies(http.BaseAddress!)[GuestSessionEndpoints.CookieName]);
        _mocks.Verify<IGuestApiClient>(client => client.GetSavedSessionAsync(FastArg.Any<string>(), FastArg.Any<CancellationToken>()), TimesSpec.NeverCalled);
        _mocks.Verify<IGuestApiClient>(client => client.ResumeAsync(FastArg.Any<string>(), FastArg.Any<CancellationToken>()), TimesSpec.NeverCalled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "Integration")]
    public async Task UnusableRedemptionResponseDoesNotSaveACookie(bool malformed)
    {
        using var http = await StartAsync();
        var state = await BootstrapAsync(http);
        Exception failure = malformed
            ? new InvalidDataException("The API returned an incomplete saved-session credential.")
            : new RpcClientRequestTimeoutException(new TimeoutException());
        _client!.Setup(client => client.RedeemAsync(Invitation, It.IsAny<CancellationToken>())).ThrowsAsync(failure);
        using var request = Mutation("/guest-session/redeem", state.GetProperty("requestToken").GetString()!);
        request.Content = JsonContent.Create(new GuestInvitation(Invitation));

        using var response = await http.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(malformed ? HttpStatusCode.BadGateway : HttpStatusCode.ServiceUnavailable, response.StatusCode);
        state = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal(malformed ? "invalid-response" : "redemption-unknown", state.GetProperty("code").GetString());
        Assert.Null(_cookies.GetCookies(http.BaseAddress!)[GuestSessionEndpoints.CookieName]);
        _mocks.Verify<IGuestApiClient>(client => client.RedeemAsync(Invitation, FastArg.Any<CancellationToken>()), TimesSpec.Once);
    }

    private async Task<HttpClient> StartAsync(bool publicOrigin = false)
    {
        _client = _mocks.GetOrCreateMock<IGuestApiClient>().AsMoq();
        _client.Setup(client => client.RedeemAsync(Invitation, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GuestRedemptionResponse("redeem", ControlResponseCodes.InvitationRedeemed, true, Credential, _clock.GetUtcNow().AddHours(24)));
        _client.Setup(client => client.GetSavedSessionAsync(Credential, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SavedGuestSession(false, _clock.GetUtcNow().AddHours(24), true));
        _client.Setup(client => client.LeaveSavedSessionAsync(Credential, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ControlResponse("leave", ControlResponseCodes.Stopped, true));
        _client.Setup(client => client.DisposeAsync()).Returns(ValueTask.CompletedTask);
        var builder = WebApplication.CreateBuilder();
        builder.Configuration["AllowedHosts"] = "*";
        builder.Configuration["OSSM:ControlClient:EndpointUri"] = "http://127.0.0.1:1";
        builder.Logging.ClearProviders();
        builder.Services.AddGuestSession();
        builder.Services.AddSingleton<TimeProvider>(_clock);
        builder.Services.AddSingleton(_mocks.GetOrCreateMock<IGuestApiClient>().Instance);
        builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
        builder.Services.AddProblemDetails();
        _app = builder.Build();
        _app.Urls.Add("http://127.0.0.1:0");
        _app.UseWebSockets();
        _app.UseGuestAccessPolicy(new() { GuestPort = 5001, Hostname = publicOrigin ? "https://guest.example" : string.Empty });
        _app.UseAuthentication();
        _app.UseAuthorization();
        _app.UseAntiforgery();
        _app.MapGuestSession();
        await _app.StartAsync(TestContext.Current.CancellationToken);
        return CreateClient(publicOrigin);
    }

    private HttpClient CreateClient(bool publicOrigin = false)
    {
        var client = new HttpClient(new HttpClientHandler { CookieContainer = _cookies, CheckCertificateRevocationList = true }, disposeHandler: true);
        try
        {
            client.BaseAddress = new(_app!.Urls.Single());
            client.DefaultRequestHeaders.Host = publicOrigin ? "guest.example" : "127.0.0.1:5001";
            if (publicOrigin)
                client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static HttpRequestMessage Mutation(string path, string requestToken, bool publicOrigin = false)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Add("Origin", publicOrigin ? "https://guest.example" : LocalOrigin);
        request.Headers.Add("X-CSRF-TOKEN", requestToken);
        return request;
    }

    private static async Task<JsonElement> BootstrapAsync(HttpClient http)
    {
        using var response = await http.GetAsync("/guest-session", TestContext.Current.CancellationToken);
        return await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
    }

    private static async Task RedeemAsync(HttpClient http)
    {
        var state = await BootstrapAsync(http);
        using var request = Mutation("/guest-session/redeem", state.GetProperty("requestToken").GetString()!);
        request.Content = JsonContent.Create(new GuestInvitation(Invitation));
        using var response = await http.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
