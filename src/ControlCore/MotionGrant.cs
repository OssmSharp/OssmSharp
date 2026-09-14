namespace OSSMDotNet.Web.ControlCore;

public sealed record MotionGrant(string Id, string Epoch, long PolicyVersion, int ValidForMs);
