namespace OssmSharp.Web.Application.Models;

/// <summary>Returns a guest-safe snapshot and its service-owned publication revision.</summary>
/// <summary>
/// Carries a guest-scoped authority snapshot and its ordering revision.
/// </summary>
/// <param name="Snapshot">The controller state to project to the guest.</param>
/// <param name="SnapshotRevision">The publication revision used to order guest updates.</param>
public sealed record GuestAuthoritySnapshot(ControlSnapshot Snapshot, long SnapshotRevision);
