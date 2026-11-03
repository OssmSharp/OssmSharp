namespace OssmSharp.Web.Api.Tests.Control.Models;

internal enum PendingSessionEventKind
{
    Settings,
    InvalidSettings,
    CompleteOldest,
    CompleteNewest,
    GuestStop,
    UserStop,
    FailedUserStop,
    Detach,
    JoinFirst,
    JoinSecond,
    Refresh,
    Expire,
    DeviceLoss,
    Reconnect,
    Observe,
    Allow,
}
