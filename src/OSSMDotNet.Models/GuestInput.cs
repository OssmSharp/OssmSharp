namespace OSSMDotNet.Models;

/// <summary>Represents one guest motion input sent to ControlService.</summary>
public sealed record GuestInput(int V, string Epoch, long PolicyVersion, string GrantId, long Seq, bool Run, ControlValues Desired);
