namespace OSSMDotNet.Models;

/// <summary>Represents a guest motion request bound to a grant, epoch, policy, and sequence.</summary>
public sealed record MotionInput(
    int V, string Epoch, long PolicyVersion, string GrantId, long Seq, bool Run, MotionSettings Desired);
