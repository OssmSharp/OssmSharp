namespace OssmSharp.Web.GuestWeb.Components.Pages;

/// <summary>
/// Represents an operation result returned to guest browser code.
/// </summary>
/// <param name="Ok">Whether the control service accepted the command.</param>
/// <param name="Code">The accepted or rejected operation code.</param>
public sealed record BrowserReply(bool Ok, string Code);
