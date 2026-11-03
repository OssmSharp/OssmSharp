namespace OssmSharp.Web.Api.Tests.Control.Models;

internal enum PendingAllowEvent
{
    FinishReset,
    GuestStop,
    UserStop,
    FailedUserStop,
    Detach,
    SecondTab,
    Refresh,
    Expire,
    DeviceLoss,
    Reconnect,
}
