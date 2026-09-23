namespace OSSMDotNet.ControlService.Models;

/// <summary>
/// Holds the service-owned guest admission, connection binding, and hashed proof.
/// </summary>
/// <param name="id">The session identifier.</param>
/// <param name="startedAt">The monotonic timestamp when the session began.</param>
/// <param name="connectionId">The admitted guest connection.</param>
/// <param name="guestHash">The hash of the guest's private admission proof.</param>
internal sealed class AuthoritySession(string id, long startedAt, string connectionId, byte[] guestHash)
{
    /// <summary>
    /// Gets the session identifier.
    /// </summary>
    internal string Id { get; } = id;
    /// <summary>
    /// Gets or sets the session's current authority mode.
    /// </summary>
    internal string Mode { get; set; } = "PendingApproval";
    /// <summary>
    /// Gets or sets the monotonic time when the session began.
    /// </summary>
    internal long StartedAt { get; set; } = startedAt;
    /// <summary>
    /// Gets or sets the admitted guest connection, if any.
    /// </summary>
    internal string? ConnectionId { get; set; } = connectionId;
    /// <summary>
    /// Gets or sets the hash of the guest admission proof, if retained.
    /// </summary>
    internal byte[]? GuestHash { get; set; } = guestHash;
}
