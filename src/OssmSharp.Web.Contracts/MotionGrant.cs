namespace OssmSharp.Web.Contracts;

/// <summary>Authorizes one fresh guest motion input for a bounded duration.</summary>
/// <summary>
/// Describes a motion grant bound to one epoch and policy version, with a millisecond validity interval.
/// </summary>
/// <param name="Id">The grant identifier bound to the guest's input.</param>
/// <param name="Epoch">The authority epoch in which the grant is valid.</param>
/// <param name="PolicyVersion">The policy version under which the grant was issued.</param>
/// <param name="ValidForMs">The grant's validity interval in milliseconds.</param>
public sealed record MotionGrant(string Id, string Epoch, long PolicyVersion, int ValidForMs);
