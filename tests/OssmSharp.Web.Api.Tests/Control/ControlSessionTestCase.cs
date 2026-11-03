using FastMoq;
using FastMoq.Extensions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using OssmSharp.Runtime;
using OssmSharp.Runtime.Oem;
using OssmSharp.Web.Api.Control;
using OssmSharp.Web.Api.Models;
using OssmSharp.Web.Api.Tests.Control.Models;
using OssmSharp.Web.Contracts;
using Xunit;
using static OssmSharp.Web.Contracts.ControlResponseCodes;

namespace OssmSharp.Web.Api.Tests.Control;

public class ControlSessionTestCase : MockerTestBase<ControlSession>, IAsyncDisposable
{
    private readonly List<Task> reads = [];
    private Mock<IOssmDevice>? device;
    private FakeTimeProvider? clock;
    private string? invitation;
    private int caseCreated;
    private int disposed;

    public string? Identity { get; private set; }
    public string? InvitationIdentity { get; private set; }
    public bool Connected { get; private set; } = true;
    public List<string> Requests { get; } = [];
    public Mock<IOssmDevice> Device => device!;
    public FakeTimeProvider Clock => clock!;
    public new ControlSession Component => base.Component;
    internal ControlSessionView? LastView { get; private set; }

    protected override Action<Mocker> SetupMocksAction => mocks =>
    {
        device = DeviceMockSetup.ConnectedDevice(mocks);
        clock = new();
        mocks.AddType<TimeProvider>(clock);
        mocks.AddType<Func<string, IOssmDevice>>(_ => device.Object);
        mocks.SetupOptions(new ControlServiceOptions { DeviceIdentifier = "device-1" });
        device.SetupGet(value => value.IsConnected).Returns(() => Connected);
        device.Setup(value => value.GetStateAsync(It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeviceState(StrokePattern.SimpleStroke, 12.5, 25, 17, 29));
        device.Setup(value => value.EmergencyStopAsync(It.IsAny<CancellationToken>()))
            .Callback(() => Requests.Add("stop")).Returns(Task.CompletedTask);
        device.Setup(value => value.SetAsync(It.IsAny<MotionSettings>(), It.IsAny<CancellationToken>()))
            .Callback<MotionSettings, CancellationToken>((settings, _) => Requests.Add($"set:{(int)settings.Pattern},{settings.Speed},{settings.Stroke},{settings.Depth},{settings.Sensation}"))
            .ReturnsAsync(new SetResult(SetStatus.Completed, 5));
    };

    protected ControlSessionTestCase CreateCase() =>
        Interlocked.Exchange(ref caseCreated, 1) == 0 ? this : new ControlSessionTestCase();

    public GuestRedemptionResponse Redeem(bool valid)
    {
        PrepareAdmission();
        var result = Component.RedeemInvitation(InvitationIdentity, new("redeem", valid ? invitation! : "invalid-token"));
        if (result.Accepted)
            Identity = Component.AuthenticateGuestSession(result.SessionToken);
        return result;
    }

    public void PrepareAdmission()
    {
        if (invitation is not null)
            return;
        var reading = PrepareAdmissionAsync();
        reads.Add(reading);
        // The tracked mock explicitly supplies completed cached reads. Conformance
        // remains synchronous; this guard rejects a future asynchronous adapter.
        Assert.True(reading.IsCompletedSuccessfully, reading.Exception?.ToString());
    }

    public bool IsAttached(string tab)
    {
        var attached = false;
        async Task ReadAsync()
        {
            attached = (await Component.GetGuestStateAsync(tab, TestContext.Current.CancellationToken)).Joined;
        }
        var reading = ReadAsync();
        reads.Add(reading);
        Assert.True(reading.IsCompletedSuccessfully, reading.Exception?.ToString());
        return attached;
    }

    public void LoseDevice()
    {
        Connected = false;
        Device.Raise(value => value.StateChanged += null, EventArgs.Empty);
    }

    public Task<ControlResponse> ReconnectAsync()
    {
        Connected = true;
        return Component.ConnectAsync("connect", TestContext.Current.CancellationToken);
    }

    internal async Task ApplyAsync(ControlSessionAction action)
    {
        var token = TestContext.Current.CancellationToken;
        string reply;
        switch (action)
        {
            case ControlSessionAction.Redeem:
                var redemption = Redeem(true);
                reply = CheckedReply(new("redeem", redemption.Code, redemption.Accepted));
                break;
            case ControlSessionAction.JoinFirst: reply = CheckedReply(Component.JoinGuest("tab-1", Identity, "join")); break;
            case ControlSessionAction.JoinSecond: reply = CheckedReply(Component.JoinGuest("tab-2", Identity, "join")); break;
            case ControlSessionAction.Allow: reply = CheckedReply(await Component.AllowGuestAsync("allow", token)); break;
            case ControlSessionAction.GuestStop: reply = CheckedReply(await Component.StopGuestAsync("tab-1", "guest-stop")); break;
            case ControlSessionAction.UserStop: reply = CheckedReply(await Component.StopAsync("user-stop")); break;
            case ControlSessionAction.Detach: await Component.GuestDisconnectedAsync("tab-1"); reply = "detached"; break;
            case ControlSessionAction.Refresh:
                reply = CheckedReply(await Component.RefreshInvitationAsync("refresh"));
                invitation = null;
                break;
            case ControlSessionAction.Expire:
                Clock.Advance(TimeSpan.FromHours(24));
                await Component.ExpireGuestAsync();
                invitation = null;
                reply = "expired";
                break;
            case ControlSessionAction.DeviceLoss: LoseDevice(); reply = "lost"; break;
            case ControlSessionAction.Reconnect:
                Connected = true;
                reply = CheckedReply(await Component.ConnectAsync("connect", token));
                break;
            default:
                var response = await Component.SubmitAsync("tab-1", new("settings", 0, action == ControlSessionAction.InvalidSubmit ? 101 : 53, 10, 17, 29), token);
                reply = response.ErrorCode ?? $"{response.Status}:{response.CommandsWritten}";
                Assert.Equal("settings", response.Correlation);
                if (response.Status == SettingsResponseStatus.Rejected)
                    Assert.Equal(0, response.CommandsWritten);
                break;
        }
        await ReadViewAsync(reply);
    }

    internal async Task ReadViewAsync(string reply)
    {
        var user = await Component.GetUserStateAsync(TestContext.Current.CancellationToken);
        var first = await Component.GetGuestStateAsync("tab-1", TestContext.Current.CancellationToken);
        Assert.Equal(50.0, user.Device!.Speed);
        Assert.Equal(25, user.Device.Stroke);
        Assert.Equal(17, user.Device.Depth);
        Assert.Equal(29, user.Device.Sensation);
        LastView = new(user.GuestJoined, user.GuestConnected, user.GuestControlAllowed, first.Joined, first.CanResumeAfterStop, user.Device.IsConnected, user.Device.TransmittedSettings, reply);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;
        try
        {
            try { await Component.DisposeAsync(); }
            finally { await Task.WhenAll(reads); }
        }
        finally { base.Dispose(); }
    }

    private static string CheckedReply(ControlResponse response)
    {
        Assert.Equal(response.Code is Joined or ControlAllowed or Stopped or InvitationRefreshed or ControlResponseCodes.Connected or InvitationRedeemed, response.Accepted);
        return response.Code;
    }

    private async Task PrepareAdmissionAsync()
    {
        var state = await Component.GetUserStateAsync(TestContext.Current.CancellationToken);
        invitation = state.Invitation.Token;
        InvitationIdentity = Component.AuthenticateInvitation(invitation);
    }
}
