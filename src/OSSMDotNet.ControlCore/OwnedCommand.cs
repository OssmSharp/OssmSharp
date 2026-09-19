using OSSMDotNet.Models;

namespace OSSMDotNet.ControlCore;

internal sealed record OwnedCommand(long Id, MotionSettings Desired, long Connection);
