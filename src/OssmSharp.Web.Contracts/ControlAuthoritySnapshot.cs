namespace OssmSharp.Web.Contracts;

/// <summary>Returns the trusted controller snapshot together with the precondition for a subsequent operator action.</summary>
/// <summary>
/// Carries operator state with its mutation precondition and ordering revision.
/// </summary>
/// <param name="Snapshot">The current controller state.</param>
/// <param name="Precondition">The state required for a subsequent operator mutation.</param>
/// <param name="SnapshotRevision">The publication revision used to order state updates.</param>
public sealed record ControlAuthoritySnapshot(ControlSnapshot Snapshot, AuthorityPrecondition Precondition, long SnapshotRevision);
