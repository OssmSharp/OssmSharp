namespace OssmSharp.Web.Contracts;

/// <summary>
/// Requests admission as the current guest.
/// </summary>
/// <param name="Correlation">The caller-provided request identifier.</param>
/// <param name="InvitationToken">The private token supplied by the user.</param>
public sealed record GuestJoinRequest(string Correlation, string InvitationToken);
