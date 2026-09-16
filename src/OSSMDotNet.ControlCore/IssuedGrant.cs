using OSSMDotNet.Models;

namespace OSSMDotNet.ControlCore;

internal sealed record IssuedGrant(MotionGrant Grant, long IssuedAt);
