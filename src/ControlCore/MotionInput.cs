using OSSMDotNet.DeviceControl;

namespace OSSMDotNet.Web.ControlCore;

public sealed record MotionInput(
    int V,
    string Epoch,
    long PolicyVersion,
    string GrantId,
    long Seq,
    bool Run,
    DesiredState Desired);
