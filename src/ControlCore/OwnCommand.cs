using OSSMDotNet.DeviceControl;

namespace OSSMDotNet.Web.ControlCore;

internal sealed record OwnCommand(long Id, DesiredState Desired, long Connection);
