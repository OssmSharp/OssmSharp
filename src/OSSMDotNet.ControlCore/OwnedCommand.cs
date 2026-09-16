using OSSMDotNet.Models;

namespace OSSMDotNet.ControlCore;

internal sealed record OwnedCommand(long Id, DesiredState Desired, long Connection);
