namespace OSSMDotNet.Web.SessionContracts;

public sealed record GuestInput(int V, string Epoch, long PolicyVersion, string GrantId, long Seq, bool Run, ControlValues Desired);
