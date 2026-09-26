namespace OssmSharp.Web.Contracts;

/// <summary>Contains the invitation proof submitted to establish a hub-scoped guest admission.</summary>
/// <summary>
/// Redeems an invitation proof for the specified session and response correlation.
/// </summary>
/// <param name="Correlation">The identifier echoed in the join response.</param>
/// <param name="SessionId">The session identified by the invitation.</param>
/// <param name="InvitationProof">The private proof authorizing admission to the session.</param>
public sealed record GuestJoinRequest(string Correlation, string SessionId, string InvitationProof);
