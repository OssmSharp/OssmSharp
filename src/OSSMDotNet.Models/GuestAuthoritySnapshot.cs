namespace OSSMDotNet.Models;

/// <summary>Returns a guest-safe snapshot and its service-owned publication revision.</summary>
public sealed record GuestAuthoritySnapshot(ControlSnapshot Snapshot, long SnapshotRevision);
