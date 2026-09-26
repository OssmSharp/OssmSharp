using OssmSharp.Web.Application.Models;

namespace OssmSharp.Web.Application.Models;

/// <summary>
/// Associates an issued motion grant with its monotonic issuance timestamp.
/// </summary>
/// <param name="Grant">The motion grant issued to the guest.</param>
/// <param name="IssuedAt">The monotonic timestamp when the grant was issued.</param>
internal sealed record IssuedGrant(MotionGrant Grant, long IssuedAt);
