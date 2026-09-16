namespace OSSMDotNet.Models;

/// <summary>Authorizes one fresh guest motion input for a bounded duration.</summary>
public sealed record MotionGrant(string Id, string Epoch, long PolicyVersion, int ValidForMs);