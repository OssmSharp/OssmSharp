namespace OSSMDotNet.AdminService;

/// <summary>Identifies the external lock revision an operator intends to clear.</summary>
public sealed record UnlockDevice(long LockRevision);
