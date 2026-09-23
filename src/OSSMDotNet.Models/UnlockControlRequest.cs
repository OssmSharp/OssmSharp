namespace OSSMDotNet.Models;

/// <summary>Identifies the external lock revision an administrator intends to clear.</summary>
/// <summary>
/// Requests removal of the external lock at the expected lock revision.
/// </summary>
/// <param name="LockRevision">The external-lock revision observed by the operator.</param>
public sealed record UnlockControlRequest(long LockRevision);
