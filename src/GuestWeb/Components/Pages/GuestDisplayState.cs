using OSSMDotNet.Web.SessionContracts;

namespace OSSMDotNet.Web.GuestWeb.Components.Pages;

public sealed record GuestDisplayState(
    ControlValues Selected, ControlValues? Sent, bool Confirmed, string Status, string? Error);
