namespace OSSMDotNet.Models;

/// <summary>Represents the guest-safe portion of a bounded motion grant.</summary>
/// <summary>
/// Describes the guest-visible motion grant and its validity interval in milliseconds.
/// </summary>
/// <param name="Id">The grant identifier bound to the guest's input.</param>
/// <param name="Epoch">The authority epoch in which the grant is valid.</param>
/// <param name="PolicyVersion">The policy version under which the grant was issued.</param>
/// <param name="ValidForMs">The grant's validity interval in milliseconds.</param>
public sealed record GuestGrant(string Id, string Epoch, long PolicyVersion, int ValidForMs);
