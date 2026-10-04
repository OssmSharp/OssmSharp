using System.Collections.Concurrent;
using System.Net;
using System.Security.Claims;
using FastMoq;
using FastMoq.Extensions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OssmSharp.Runtime.Oem;
using OssmSharp.Web.Api.Control;
using OssmSharp.Web.Api.Models;
using OssmSharp.Web.Api.SignalR;
using OssmSharp.Web.Client.Clients;
using OssmSharp.Web.Client.Connections;
using OssmSharp.Web.Client.Models;
using OssmSharp.Web.Contracts;
using Xunit;

namespace OssmSharp.Web.Api.Tests;

public sealed class GuestApiClientConnectionTests : MockerTestBase<GuestApiClient>, IAsyncDisposable
{
    private const string AdminKey = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private RpcClientOptions _options = null!;
    private RpcClientTestScope _rpcScope = null!;
    private ControlSession _session = null!;
    private FakeOssmDevice _device = null!;

    protected override Action<Mocker> SetupMocksAction => mocks =>
    {
        _device = new();
        mocks.AddType<Func<string, IOssmDevice>>(_ => _device);
        mocks.AddType(TimeProvider.System);
        mocks.SetupOptions(new ControlServiceOptions { DeviceIdentifier = "test", AdminWebServiceKey = AdminKey });
        _session = mocks.CreateInstance<ControlSession>()!;
        _options = new() { EndpointUri = "http://127.0.0.1:1" };
        _rpcScope = new(_options, guest: true);
        mocks.AddType<IRpcClient>(_rpcScope.Connection);
    };

    public async ValueTask DisposeAsync()
    {
        try { await Component.DisposeAsync(); }
        finally
        {
            try
            {
                await _session.DisposeAsync();
            }
            finally
            {
                try
                {
                    if (_device.Disposals == 0)
                    {
                        await _device.DisposeAsync();
                    }
                }
                finally
                {
                    try { await _rpcScope.DisposeAsync(); }
                    finally { base.Dispose(); }
                }
            }
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task InvitationAuthenticatesBothHandshakesAndExistingConnectionSurvivesRotation()
    {
        var identities = new ConcurrentQueue<ClaimsPrincipal>();
        await using var app = await StartApiAsync(identities);
        var invitation = (await _session.GetUserStateAsync(TestContext.Current.CancellationToken)).Invitation;
        var identity = _session.AuthenticateInvitation(invitation.Token);

        Assert.True((await Component.JoinAsync(invitation.Token, TestContext.Current.CancellationToken)).Accepted);
        var state = await Component.GetStateAsync(TestContext.Current.CancellationToken);
        Assert.True(state.Joined);
        Assert.False(state.ControlAllowed);
        var unapproved = await Component.SubmitAsync(new("before-allow", 0, 10, 20, 30, 40), TestContext.Current.CancellationToken);
        Assert.Equal(SettingsResponseStatus.Rejected, unapproved.Status);
        Assert.Equal(0, _device.Sets);
        Assert.Equal(2, identities.Count);
        foreach (var principal in identities)
        {
            Assert.Equal("Bearer", principal.Identity!.AuthenticationType);
            Assert.Equal(identity, principal.FindFirstValue(ClaimTypes.NameIdentifier));
            Assert.True(principal.IsInRole("Guest"));
            Assert.DoesNotContain(principal.Claims, claim => claim.Value == invitation.Token);
        }
        await using var replayScope = new RpcClientTestScope(_options, guest: true);
        await using var replay = new GuestApiClient(replayScope.Connection);
        var rejection = await Assert.ThrowsAsync<ApiClientException>(
            () => replay.JoinAsync(invitation.Token, TestContext.Current.CancellationToken));
        Assert.Equal("invalid-invitation", rejection.Code);
        Assert.True((await Component.GetStateAsync(TestContext.Current.CancellationToken)).Joined);
        Assert.True((await _session.AllowGuestAsync("allow", TestContext.Current.CancellationToken)).Accepted);
        Assert.True((await Component.RenewAsync(TestContext.Current.CancellationToken)).Accepted);
        Assert.Equal(
            SettingsResponseStatus.Completed,
            (await Component.SubmitAsync(new("set", 0, 10, 20, 30, 40), TestContext.Current.CancellationToken)).Status);
        Assert.Equal(1, _device.Sets);
        Assert.True((await Component.LeaveAsync(TestContext.Current.CancellationToken)).Accepted);
        Assert.Equal(1, _device.Stops);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task HubSeparatesAdminAuthorityAndStaleGuestIdentity()
    {
        await using var app = await StartApiAsync();
        var invitation = (await _session.GetUserStateAsync(TestContext.Current.CancellationToken)).Invitation;
        await using var stale = new SignalRRpcClientFactory(Options.Create(_options)).CreateForGuest(invitation.Token);
        await stale.StartAsync(TestContext.Current.CancellationToken);
        Assert.True((await Component.JoinAsync(invitation.Token, TestContext.Current.CancellationToken)).Accepted);
        await Assert.ThrowsAsync<HubException>(() => stale.InvokeAsync<UserSessionState>("GetUserState", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<HubException>(() => stale.InvokeAsync<ControlResponse>("Stop", "stop", TestContext.Current.CancellationToken));
        Assert.Null((await stale.InvokeAsync<GuestSessionState>("GetGuestState", TestContext.Current.CancellationToken)).Device);

        await using var adminScope = new RpcClientTestScope(new() { EndpointUri = _options.EndpointUri, ServiceKey = AdminKey });
        await using var admin = new AdminApiClient(adminScope.Connection);
        Assert.True((await admin.GetStateAsync(TestContext.Current.CancellationToken)).GuestJoined);
        Assert.True((await admin.EndGuestAsync(TestContext.Current.CancellationToken)).Accepted);
        var fresh = (await admin.GetStateAsync(TestContext.Current.CancellationToken)).Invitation;
        var rejected = await stale.InvokeAsync<ControlResponse>("Join", new GuestJoinRequest("join", fresh.Token), TestContext.Current.CancellationToken);
        Assert.False(rejected.Accepted);
        Assert.Equal("invalid-invitation", rejected.Code);
        Assert.False((await Component.GetStateAsync(TestContext.Current.CancellationToken)).Joined);
        Assert.Equal(1, _device.Stops);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("ServiceKey BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBE")]
    [InlineData("Bearer malformed")]
    [Trait("Category", "Integration")]
    public async Task UnauthenticatedNegotiationCannotUseGuestServiceOrQueryCredentials(string? authorization)
    {
        await using var app = await StartApiAsync();
        var invitation = (await _session.GetUserStateAsync(TestContext.Current.CancellationToken)).Invitation;
        using var http = new HttpClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{_options.EndpointUri}/hubs/control/v2/negotiate?negotiateVersion=1&access_token={invitation.Token}");
        if (authorization is not null)
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        using var response = await http.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.False((await _session.GetUserStateAsync(TestContext.Current.CancellationToken)).GuestJoined);
    }

    [Fact]
    public async Task GuestMethodsBeforeJoinDoNotConnect()
    {
        var error = await Assert.ThrowsAsync<ApiClientException>(() => Component.GetStateAsync(TestContext.Current.CancellationToken));
        Assert.Equal("guest-not-joined", error.Code);
    }

    [Theory]
    [InlineData("invite\r\nInjected: value")]
    [InlineData("invite with spaces")]
    [InlineData("invite!invalid")]
    public async Task MalformedInvitationIsRejectedBeforeConnecting(string invitation)
    {
        var error = await Assert.ThrowsAsync<ApiClientException>(
            () => Component.JoinAsync(invitation, TestContext.Current.CancellationToken));
        Assert.Equal("invalid-invitation", error.Code);
    }

    [Fact]
    public async Task OversizedInvitationIsRejectedBeforeConnecting()
    {
        var error = await Assert.ThrowsAsync<ApiClientException>(
            () => Component.JoinAsync(new string('A', 129), TestContext.Current.CancellationToken));
        Assert.Equal("invalid-invitation", error.Code);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task DisconnectRevokesGuestAndStopsDevice()
    {
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var app = await StartApiAsync(disconnected: disconnected);
        var invitation = (await _session.GetUserStateAsync(TestContext.Current.CancellationToken)).Invitation;
        await Component.JoinAsync(invitation.Token, TestContext.Current.CancellationToken);
        Assert.True((await _session.AllowGuestAsync("allow", TestContext.Current.CancellationToken)).Accepted);

        await Component.DisposeAsync();
        await disconnected.Task.WaitAsync(TestContext.Current.CancellationToken);

        var state = await _session.GetUserStateAsync(TestContext.Current.CancellationToken);
        Assert.False(state.GuestJoined);
        Assert.False(state.GuestControlAllowed);
        Assert.Equal(1, _device.Stops);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task MultipleAuthorizationValuesCannotAuthenticate()
    {
        await using var app = await StartApiAsync();
        var invitation = (await _session.GetUserStateAsync(TestContext.Current.CancellationToken)).Invitation;
        using var http = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, _options.EndpointUri + "/hubs/control/v2/negotiate?negotiateVersion=1");
        request.Headers.TryAddWithoutValidation("Authorization", [$"Bearer {invitation.Token}", $"ServiceKey {AdminKey}"]);
        using var response = await http.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData(401, "invalid-invitation")]
    [InlineData(503, "connection-unavailable")]
    [Trait("Category", "Integration")]
    public async Task AuthenticationRejectionIsDistinctFromTransportFailure(int status, string code)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        await using var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");
        app.Run(context =>
        {
            context.Response.StatusCode = status;
            return Task.CompletedTask;
        });
        await app.StartAsync(TestContext.Current.CancellationToken);
        _options.EndpointUri = app.Urls.Single();
        var error = await Assert.ThrowsAsync<ApiClientException>(
            () => Component.JoinAsync(new string('A', 43), TestContext.Current.CancellationToken));
        Assert.Equal(code, error.Code);
    }

    private static void UseConnectionTracking(
        WebApplication app,
        ConcurrentQueue<ClaimsPrincipal>? identities,
        TaskCompletionSource? disconnected) => app.Use(async (context, next) =>
    {
        var controlConnection = context.Request.Path == "/hubs/control/v2" && context.WebSockets.IsWebSocketRequest;
        if (context.User.Identity?.IsAuthenticated == true)
            identities?.Enqueue(context.User);
        await next(context);
        if (controlConnection)
            disconnected?.TrySetResult();
    });

    private async Task<WebApplication> StartApiAsync(
        ConcurrentQueue<ClaimsPrincipal>? identities = null,
        TaskCompletionSource? disconnected = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.Configure<ControlServiceOptions>(options => options.AdminWebServiceKey = AdminKey);
        builder.Services.AddSingleton<IControlSession>(_session);
        builder.Services.AddApiAuthentication().AddApiAuthorization().AddApiHub();
        var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");
        app.UseWebSockets();
        app.UseAuthentication();
        app.UseAuthorization();
        UseConnectionTracking(app, identities, disconnected);
        app.MapControlHubV2();
        try
        {
            await app.StartAsync(TestContext.Current.CancellationToken);
            _options.EndpointUri = app.Urls.Single();
            return app;
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }
    }
}
