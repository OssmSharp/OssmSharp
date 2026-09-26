namespace OssmSharp.Web.Contracts;

/// <summary>Represents the reusable invitation issued by ControlService.</summary>
/// <summary>
/// Describes the invitation link and its session and reachability constraints.
/// </summary>
/// <param name="SessionId">The session identified by this invitation.</param>
/// <param name="Url">The link presented to the guest.</param>
/// <param name="ExpiresAt">The expiry time, or null for a non-expiring invitation.</param>
/// <param name="LocalOnly">Whether the invitation is restricted to local access.</param>
public sealed record Invitation(string SessionId, string Url, DateTimeOffset? ExpiresAt, bool LocalOnly);
