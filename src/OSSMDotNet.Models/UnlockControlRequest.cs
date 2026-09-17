namespace OSSMDotNet.Models;

/// <summary>Identifies the external lock revision an administrator intends to clear.</summary>
public sealed record UnlockControlRequest(long LockRevision);
