namespace OSSMDotNet.Models;

/// <summary>Returns the trusted controller snapshot together with the precondition for a subsequent operator action.</summary>
public sealed record ControlAuthoritySnapshot(ControlSnapshot Snapshot, AuthorityPrecondition Precondition, long SnapshotRevision);
