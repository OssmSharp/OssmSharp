using System.Collections.Concurrent;
using System.Net;
using System.Security.Claims;
using FastMoq;
using FastMoq.Extensions;
using FastMoq.Providers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using OssmSharp.Runtime;
using OssmSharp.Runtime.Oem;
using OssmSharp.Web.Api.Authentication;
using OssmSharp.Web.Api.Control;
using OssmSharp.Web.Api.Models;
using OssmSharp.Web.Api.SignalR;
using OssmSharp.Web.Client.Clients;
using OssmSharp.Web.Client.Connections;
using OssmSharp.Web.Client.Models;
using OssmSharp.Web.Contracts;
using Shiny.BluetoothLE;
using Xunit;

namespace OssmSharp.Web.Api.Tests;

public sealed class GuestApiClientConnectionTests : MockerTestBase<GuestApiClient>, IAsyncDisposable
{
    private const string AdminKey = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private readonly FakeTimeProvider _clock = new();
    private RpcClientOptions _options = null!;
    private RpcClientTestScope _rpcScope = null!;
    private ControlSession _session = null!;
    private Mock<IOssmDevice> _device = null!;

    protected override Action<Mocker> SetupMocksAction => mocks =>
    {
        _device = DeviceMockSetup.ConnectedDevice(mocks);
        mocks.AddType<Func<string, IOssmDevice>>(_ => _device.Object);
        mocks.AddType(TimeProvider.System);
        mocks.SetupOptions(new ControlServiceOptions { DeviceIdentifier = "test", AdminWebServiceKey = AdminKey });
        _session = mocks.CreateInstance<ControlSession>()!;
        _options = new() { EndpointUri = "http://127.0.0.1:1" };
        _rpcScope = new(_options, guest: true, timeProvider: _clock);
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
                _device.Verify<ValueTask>(device => device.DisposeAsync(), Times.Once);
                _device.VerifyAdd(device => device.StateChanged += It.IsAny<EventHandler>(), Times.Once);
                _device.VerifyRemove(device => device.StateChanged -= It.IsAny<EventHandler>(), Times.Once);
            }
            finally
            {
                try { await _rpcScope.DisposeAsync(); }
                finally { base.Dispose(); }
            }
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [Trait("Category", "Integration")]
    public async Task InvalidSavedCredentialDoesNotConsumeConnectionStartup(string? credential)
    {
        var token = TestContext.Current.CancellationToken;
        var error = await Assert.ThrowsAnyAsync<ArgumentException>(
            () => _rpcScope.Connection.StartSessionAsync(credential!, token));
        Assert.Equal("sessionToken", error.ParamName);

        await using var app = await StartApiAsync();
        var invitation = (await _session.GetUserStateAsync(token)).Invitation;
        await JoinAsync(invitation.Token, token);
        var state = await Component.GetStateAsync(token);

        Assert.True(state.Joined);
        Assert.False(state.ControlAllowed);
        Mocks.Verify<IOssmDevice>(device => device.EmergencyStopAsync(FastArg.Any<CancellationToken>()), TimesSpec.NeverCalled);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task SettingsCanCompleteBeyondTheOrdinaryRequestDeadline()
    {
        await using var app = await StartApiAsync();
        var token = TestContext.Current.CancellationToken;
        var invitation = (await _session.GetUserStateAsync(token)).Invitation;
        await JoinAsync(invitation.Token, token);
        await _session.AllowGuestAsync("allow", token);
        var (entered, completed) = HoldDeviceSettings();
        var pending = Component.SubmitAsync(new("first", 0, 57, 37, 100, 50), token);
        try
        {
            await entered.WaitAsync(token);
            _clock.Advance(TimeSpan.FromSeconds(3));
            completed.TrySetResult(new(SetStatus.Completed, 5));
            Assert.Equal(SettingsResponseStatus.Completed, (await pending).Status);
            Assert.Equal(
                SettingsResponseStatus.Completed,
                (await Component.SubmitAsync(new("second", 0, 57, 73, 100, 50), token)).Status);
            Mocks.Verify<IOssmDevice>(device => device.EmergencyStopAsync(FastArg.Any<CancellationToken>()), TimesSpec.NeverCalled);
        }
        finally
        {
            completed.TrySetResult(new(SetStatus.Completed, 5));
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task DisposalInterruptsPendingSettingsAndQueuedState()
    {
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var app = await StartApiAsync(disconnected: disconnected);
        var token = TestContext.Current.CancellationToken;
        var invitation = (await _session.GetUserStateAsync(token)).Invitation;
        await JoinAsync(invitation.Token, token);
        await _session.AllowGuestAsync("allow", token);
        Assert.False(disconnected.Task.IsCompleted);
        var (entered, completed) = HoldDeviceSettings();
        var settings = Component.SubmitAsync(new("pending", 0, 57, 37, 100, 50), token);
        await entered.WaitAsync(token);
        var state = Component.GetStateAsync(token);
        var disposal = Component.DisposeAsync().AsTask();
        try
        {
            var settingsError = await Assert.ThrowsAsync<ApiClientException>(
                () => settings.WaitAsync(TimeSpan.FromSeconds(5), token));
            var stateError = await Assert.ThrowsAsync<ApiClientException>(
                () => state.WaitAsync(TimeSpan.FromSeconds(5), token));
            Assert.Equal("connection-unavailable", settingsError.Code);
            Assert.Equal("connection-unavailable", stateError.Code);
            await disposal.WaitAsync(TimeSpan.FromSeconds(5), token);
            Assert.False(completed.Task.IsCompleted);
            await disconnected.Task.WaitAsync(token);
            var saved = await _session.GetUserStateAsync(token);
            Assert.True(saved.GuestJoined);
            Assert.False(saved.GuestConnected);
            Assert.True(saved.GuestControlAllowed);
            Mocks.Verify<IOssmDevice>(device => device.EmergencyStopAsync(FastArg.Any<CancellationToken>()), TimesSpec.Once);
        }
        finally
        {
            completed.TrySetResult(new(SetStatus.Completed, 5));
            try { await Task.WhenAll(settings, state); }
            catch (ApiClientException) { }
            await disposal;
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task CallerCancellationStillCancelsSettingsResponseWait()
    {
        await using var app = await StartApiAsync();
        var token = TestContext.Current.CancellationToken;
        var invitation = (await _session.GetUserStateAsync(token)).Invitation;
        await JoinAsync(invitation.Token, token);
        await _session.AllowGuestAsync("allow", token);
        var (entered, completed) = HoldDeviceSettings();
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(token);
        var settings = Component.SubmitAsync(new("pending", 0, 57, 37, 100, 50), caller.Token);
        try
        {
            await entered.WaitAsync(token);
            await caller.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => settings);
            Assert.False(completed.Task.IsCompleted);
            Mocks.Verify<IOssmDevice>(device => device.EmergencyStopAsync(FastArg.Any<CancellationToken>()), TimesSpec.NeverCalled);
        }
        finally
        {
            completed.TrySetResult(new(SetStatus.Completed, 5));
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RedemptionAndAttachmentUseSeparateAuthorityAndConsumeTheInvitation()
    {
        var identities = new ConcurrentQueue<ClaimsPrincipal>();
        await using var app = await StartApiAsync(identities);
        var invitation = (await _session.GetUserStateAsync(TestContext.Current.CancellationToken)).Invitation;
        var identity = _session.AuthenticateInvitation(invitation.Token);

        var redeemed = await RedeemAsync(invitation.Token, TestContext.Current.CancellationToken);
        Assert.True((await Component.ResumeAsync(redeemed.SessionToken!, TestContext.Current.CancellationToken)).Accepted);
        var state = await Component.GetStateAsync(TestContext.Current.CancellationToken);
        Assert.True(state.Joined);
        Assert.False(state.ControlAllowed);
        var unapproved = await Component.SubmitAsync(new("before-allow", 0, 10, 20, 30, 40), TestContext.Current.CancellationToken);
        Assert.Equal(SettingsResponseStatus.Rejected, unapproved.Status);
        Mocks.Verify<IOssmDevice>(device => device.SetAsync(FastArg.Any<MotionSettings>(), FastArg.Any<CancellationToken>()), TimesSpec.NeverCalled);
        Assert.Equal(4, identities.Count);
        foreach (var principal in identities)
        {
            Assert.Contains(principal.Identity!.AuthenticationType, new[] { "Bearer", "GuestSession" });
            Assert.Equal(identity, principal.FindFirstValue(ClaimTypes.NameIdentifier));
            Assert.True(principal.IsInRole(principal.Identity.AuthenticationType == "Bearer" ? "Invitation" : "Guest"));
            Assert.DoesNotContain(principal.Claims, claim => claim.Value == invitation.Token);
            Assert.DoesNotContain(principal.Claims, claim => claim.Value == redeemed.SessionToken);
        }
        await using var replayScope = new RpcClientTestScope(_options, guest: true);
        await using var replay = new GuestApiClient(replayScope.Connection);
        var rejection = await Assert.ThrowsAsync<ApiClientException>(
            () => replay.RedeemAsync(invitation.Token, TestContext.Current.CancellationToken));
        Assert.Equal("invalid-invitation", rejection.Code);
        Assert.True((await Component.GetStateAsync(TestContext.Current.CancellationToken)).Joined);
        Assert.True((await _session.AllowGuestAsync("allow", TestContext.Current.CancellationToken)).Accepted);
        Assert.True((await Component.RenewAsync(TestContext.Current.CancellationToken)).Accepted);
        Assert.Equal(
            SettingsResponseStatus.Completed,
            (await Component.SubmitAsync(new("set", 0, 10, 20, 30, 40), TestContext.Current.CancellationToken)).Status);
        Mocks.Verify<IOssmDevice>(device => device.SetAsync(FastArg.Any<MotionSettings>(), FastArg.Any<CancellationToken>()), TimesSpec.Once);
        Assert.True((await Component.LeaveAsync(TestContext.Current.CancellationToken)).Accepted);
        Mocks.Verify<IOssmDevice>(device => device.EmergencyStopAsync(FastArg.Any<CancellationToken>()), TimesSpec.Once);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task HubSeparatesAdminAuthorityAndStaleGuestIdentity()
    {
        await using var app = await StartApiAsync();
        var invitation = (await _session.GetUserStateAsync(TestContext.Current.CancellationToken)).Invitation;
        await using var stale = new SignalRHubConnectionFactory(Options.Create(_options)).CreateForGuest(invitation.Token);
        await stale.StartAsync(TestContext.Current.CancellationToken);
        Assert.True((await JoinAsync(invitation.Token, TestContext.Current.CancellationToken)).Accepted);
        await Assert.ThrowsAsync<HubException>(() => stale.InvokeAsync<UserSessionState>("GetUserState", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<HubException>(() => stale.InvokeAsync<ControlResponse>("Stop", "stop", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<HubException>(() => stale.InvokeAsync<ControlResponse>("ResetPosition", "position", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<HubException>(() => stale.InvokeAsync<ControlResponse>("SetPreviewDimensions", "preview", new PreviewDimensions(50, 25), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<HubException>(() => stale.InvokeAsync<ControlResponse>("GuestResetPosition", "position", TestContext.Current.CancellationToken));
        Mocks.Verify<IOssmDevice>(device => device.ResetPositionAsync(FastArg.Any<CancellationToken>()), TimesSpec.NeverCalled);
        await Assert.ThrowsAsync<HubException>(() => stale.InvokeAsync<GuestSessionState>("GetGuestState", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<HubException>(() => stale.InvokeAsync<SavedGuestSession>("GetSavedSession", TestContext.Current.CancellationToken));

        await using var adminScope = new RpcClientTestScope(new() { EndpointUri = _options.EndpointUri, ServiceKey = AdminKey });
        await using var admin = new AdminApiClient(adminScope.Connection);
        Assert.True((await admin.GetStateAsync(TestContext.Current.CancellationToken)).GuestJoined);
        var dimensions = new PreviewDimensions(50, 25.5);
        Assert.True((await admin.SetPreviewDimensionsAsync(dimensions, TestContext.Current.CancellationToken)).Accepted);
        Assert.Equal(dimensions, (await Component.GetStateAsync(TestContext.Current.CancellationToken)).Preview);
        Assert.Equal(dimensions, (await admin.GetStateAsync(TestContext.Current.CancellationToken)).Preview);
        var invalid = await Assert.ThrowsAsync<ApiClientException>(() => admin.SetPreviewDimensionsAsync(new(0, 25), TestContext.Current.CancellationToken));
        Assert.Equal(ControlResponseCodes.InvalidPreviewDimensions, invalid.Code);
        Assert.True((await admin.EndGuestAsync(TestContext.Current.CancellationToken)).Accepted);
        var fresh = (await admin.GetStateAsync(TestContext.Current.CancellationToken)).Invitation;
        var rejected = await stale.InvokeAsync<GuestRedemptionResponse>("Redeem", new GuestJoinRequest("join", fresh.Token), TestContext.Current.CancellationToken);
        Assert.False(rejected.Accepted);
        Assert.Equal("invalid-invitation", rejected.Code);
        Assert.False((await Component.GetStateAsync(TestContext.Current.CancellationToken)).Joined);
        Mocks.Verify<IOssmDevice>(device => device.EmergencyStopAsync(FastArg.Any<CancellationToken>()), TimesSpec.Once);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "Integration")]
    public async Task ResetPositionAllowsStopThroughSameClientAndHub(bool adminRequest)
    {
        await using var app = await StartApiAsync();
        await using var adminScope = new RpcClientTestScope(new() { EndpointUri = _options.EndpointUri, ServiceKey = AdminKey });
        await using var admin = new AdminApiClient(adminScope.Connection);
        if (!adminRequest)
        {
            var invitation = (await _session.GetUserStateAsync(TestContext.Current.CancellationToken)).Invitation;
            await JoinAsync(invitation.Token, TestContext.Current.CancellationToken);
            var denied = await Assert.ThrowsAsync<ApiClientException>(() => Component.ResetPositionAsync(TestContext.Current.CancellationToken));
            Assert.Equal("control-not-allowed", denied.Code);
            _device.Verify(device => device.ResetPositionAsync(It.IsAny<CancellationToken>()), Times.Never);
            Assert.True((await admin.AllowGuestAsync(TestContext.Current.CancellationToken)).Accepted);
        }

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _device.Setup(device => device.ResetPositionAsync(It.IsAny<CancellationToken>()))
            .Callback(() => entered.TrySetResult()).Returns(pending.Task);
        _device.Setup(device => device.EmergencyStopAsync(It.IsAny<CancellationToken>()))
            .Callback(() => pending.TrySetCanceled(CancellationToken.None)).Returns(Task.CompletedTask);
        var reset = adminRequest ? admin.ResetPositionAsync(TestContext.Current.CancellationToken) : Component.ResetPositionAsync(TestContext.Current.CancellationToken);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.False(reset.IsCompleted);
            var stopped = await (adminRequest ? admin.StopAsync(TestContext.Current.CancellationToken) : Component.StopAsync(TestContext.Current.CancellationToken));
            Assert.True(stopped.Accepted);
            var error = await Assert.ThrowsAsync<ApiClientException>(() => reset);
            Assert.Equal("cancelled", error.Code);
            _device.Verify(device => device.EmergencyStopAsync(It.IsAny<CancellationToken>()), Times.Once);
            _device.Verify(device => device.ResetPositionAsync(It.IsAny<CancellationToken>()), Times.Once);
            Assert.False((await _session.GetUserStateAsync(TestContext.Current.CancellationToken)).Device!.IsResettingPosition);
            _device.Verify(device => device.SetAsync(It.IsAny<MotionSettings>(), It.IsAny<CancellationToken>()), Times.Never);
        }
        finally
        {
            pending.TrySetResult();
            try { await reset; }
            catch (ApiClientException) { }
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ResetPositionHubRejectsInvalidCorrelationWithoutDeviceCall()
    {
        await using var app = await StartApiAsync();
        var invitation = (await _session.GetUserStateAsync(TestContext.Current.CancellationToken)).Invitation;
        var redeemed = await RedeemAsync(invitation.Token, TestContext.Current.CancellationToken);
        await using var connection = new SignalRHubConnectionFactory(Options.Create(_options)).CreateForGuestSession(redeemed.SessionToken!);
        await connection.StartAsync(TestContext.Current.CancellationToken);
        Assert.True((await connection.InvokeAsync<ControlResponse>("Join", "join", TestContext.Current.CancellationToken)).Accepted);
        Assert.True((await _session.AllowGuestAsync("allow", TestContext.Current.CancellationToken)).Accepted);
        foreach (var correlation in new[] { string.Empty, new string('x', 65), "bad\ncorrelation" })
        {
            var result = await connection.InvokeAsync<ControlResponse>("GuestResetPosition", correlation, TestContext.Current.CancellationToken);
            Assert.Equal(new ControlResponse(correlation, "invalid-correlation", false), result);
        }
        _device.Verify(device => device.ResetPositionAsync(It.IsAny<CancellationToken>()), Times.Never);
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
            () => Component.RedeemAsync(invitation, TestContext.Current.CancellationToken));
        Assert.Equal("invalid-invitation", error.Code);
    }

    [Fact]
    public async Task OversizedInvitationIsRejectedBeforeConnecting()
    {
        var error = await Assert.ThrowsAsync<ApiClientException>(
            () => Component.RedeemAsync(new string('A', 129), TestContext.Current.CancellationToken));
        Assert.Equal("invalid-invitation", error.Code);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task DisconnectDetachesGuestAndStopsDeviceWithoutRevokingSavedPermission()
    {
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var app = await StartApiAsync(disconnected: disconnected);
        var invitation = (await _session.GetUserStateAsync(TestContext.Current.CancellationToken)).Invitation;
        await JoinAsync(invitation.Token, TestContext.Current.CancellationToken);
        Assert.True((await _session.AllowGuestAsync("allow", TestContext.Current.CancellationToken)).Accepted);
        Assert.False(disconnected.Task.IsCompleted);

        await Component.DisposeAsync();
        await disconnected.Task.WaitAsync(TestContext.Current.CancellationToken);

        var state = await _session.GetUserStateAsync(TestContext.Current.CancellationToken);
        Assert.True(state.GuestJoined);
        Assert.True(state.GuestControlAllowed);
        Assert.False(state.GuestConnected);
        Mocks.Verify<IOssmDevice>(device => device.EmergencyStopAsync(FastArg.Any<CancellationToken>()), TimesSpec.Once);
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
            () => Component.RedeemAsync(new string('A', 43), TestContext.Current.CancellationToken));
        Assert.Equal(code, error.Code);
    }

    private static void UseConnectionTracking(
        WebApplication app,
        ConcurrentQueue<ClaimsPrincipal>? identities,
        TaskCompletionSource? disconnected) => app.Use(async (context, next) =>
    {
        var guestSessionConnection = context.Request.Path == "/hubs/control/v2" && context.WebSockets.IsWebSocketRequest
            && context.User.IsInRole(ControlServiceRoles.GuestWeb);
        if (context.User.Identity?.IsAuthenticated == true)
            identities?.Enqueue(context.User);
        await next(context);
        if (guestSessionConnection)
            disconnected?.TrySetResult();
    });

    private async Task<GuestRedemptionResponse> RedeemAsync(string invitation, CancellationToken token)
    {
        await using var scope = new RpcClientTestScope(_options, guest: true, timeProvider: _clock);
        await using var client = new GuestApiClient(scope.Connection);
        return await client.RedeemAsync(invitation, token);
    }

    private async Task<ControlResponse> JoinAsync(string invitation, CancellationToken token)
    {
        var redeemed = await RedeemAsync(invitation, token);
        return await Component.ResumeAsync(redeemed.SessionToken!, token);
    }

    private (Task Entered, TaskCompletionSource<SetResult> Completed) HoldDeviceSettings()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource<SetResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _device.Setup(device => device.SetAsync(It.IsAny<MotionSettings>(), It.IsAny<CancellationToken>()))
            .Callback(() => entered.TrySetResult()).Returns(completed.Task);
        return (entered.Task, completed);
    }

    private async Task<WebApplication> StartApiAsync(
        ConcurrentQueue<ClaimsPrincipal>? identities = null,
        TaskCompletionSource? disconnected = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(Mocks.GetOrCreateMock<IBleManager>().Instance);
        builder.Services.AddApiServices().AddSignalR();
        builder.Services.Configure<ControlServiceOptions>(options => options.AdminWebServiceKey = AdminKey);
        builder.Services.AddSingleton<IControlSession>(_session);
        builder.Services.AddSingleton<Func<string, IOssmDevice>>(_ => _device.Object);
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
