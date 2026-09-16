namespace OSSMDotNet.GuestWeb.Components.Pages;

/// <summary>Represents a browser-visible operation result.</summary>
public sealed record BrowserReply(bool Ok, string Code);
