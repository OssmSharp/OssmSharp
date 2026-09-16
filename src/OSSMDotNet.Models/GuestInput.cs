namespace OSSMDotNet.Models;

/// <summary>Represents one guest motion input forwarded to the admin service.</summary>
public sealed record GuestInput(int V, string Epoch, long PolicyVersion, string GrantId, long Seq, bool Run, ControlValues Desired);