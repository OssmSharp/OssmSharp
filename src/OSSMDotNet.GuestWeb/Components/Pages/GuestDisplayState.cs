using OSSMDotNet.Models;

namespace OSSMDotNet.GuestWeb.Components.Pages;

/// <summary>Represents the guest control values and feedback currently rendered by the page.</summary>
public sealed record GuestDisplayState(
    ControlValues Selected, ControlValues? Sent, bool Confirmed, string Status, string? Error);
