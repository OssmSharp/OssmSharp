using OssmSharp.Web.Contracts;
using static OssmSharp.Web.Contracts.ControlResponseCodes;

namespace OssmSharp.Web.Api.Tests.Control.Models;

// Independent contract model: no production transitions, mock answers, or observed result copying.
internal sealed class ControlSessionReference
{
    private bool connected = true;
    private bool stopped;
    private DeviceSettingsSnapshot? sent;

    public bool Admitted { get; private set; }
    public bool Allowed { get; private set; }
    public int Tab { get; private set; }
    public string Reply { get; private set; } = "";
    public List<string> Requests { get; } = [];
    public ControlSessionView View => new(Admitted, Tab != 0, Allowed, Tab == 1, Tab == 1 && Allowed && stopped, connected, connected ? sent : null, Reply);

    public void Apply(ControlSessionAction action)
    {
        switch (action)
        {
            case ControlSessionAction.Redeem:
                Reply = Admitted ? GuestAlreadyJoined : InvitationRedeemed;
                Admitted = true;
                break;
            case ControlSessionAction.JoinFirst:
            case ControlSessionAction.JoinSecond:
                Reply = !Admitted ? InvalidGuestSession : Tab != 0 ? GuestAlreadyJoined : Joined;
                if (Admitted && Tab == 0)
                    Tab = action == ControlSessionAction.JoinFirst ? 1 : 2;
                break;
            case ControlSessionAction.Allow:
                Reply = !Admitted ? GuestNotJoined : !connected ? DeviceNotReady : ControlAllowed;
                if (Admitted && connected)
                {
                    Allowed = true;
                    stopped = false;
                }
                break;
            case ControlSessionAction.GuestStop:
                Reply = Tab != 1 ? GuestNotJoined : !connected ? DeviceNotConnected : Stopped;
                if (Tab == 1 && connected)
                {
                    Requests.Add("stop");
                    stopped = Allowed;
                }
                break;
            case ControlSessionAction.UserStop:
                Allowed = stopped = false;
                Reply = connected ? Stopped : DeviceNotConnected;
                if (connected)
                    Requests.Add("stop");
                break;
            case ControlSessionAction.Detach:
                if (Tab == 1)
                {
                    Tab = 0;
                    sent = null;
                    stopped = Allowed;
                    if (connected)
                        Requests.Add("stop");
                }
                Reply = "detached";
                break;
            case ControlSessionAction.Refresh:
            case ControlSessionAction.Expire:
                Reply = action == ControlSessionAction.Refresh ? !connected && Admitted ? DeviceNotConnected : InvitationRefreshed : "expired";
                if (Admitted && connected)
                    Requests.Add("stop");
                Admitted = Allowed = stopped = false;
                Tab = 0;
                sent = null;
                break;
            case ControlSessionAction.DeviceLoss:
                connected = Allowed = stopped = false;
                sent = null;
                Reply = "lost";
                break;
            case ControlSessionAction.Reconnect:
                connected = true;
                Reply = Connected;
                break;
            case ControlSessionAction.Submit:
            case ControlSessionAction.InvalidSubmit:
                Reply = action == ControlSessionAction.InvalidSubmit ? InvalidSettings : Tab != 1 ? GuestNotJoined : !Allowed ? ControlNotAllowed : "Completed:5";
                if (action == ControlSessionAction.Submit && Tab == 1 && Allowed)
                {
                    Requests.Add("set:0,53,10,17,29");
                    sent = new(0, 53, 10, 17, 29);
                    stopped = false;
                }
                break;
        }
    }
}
