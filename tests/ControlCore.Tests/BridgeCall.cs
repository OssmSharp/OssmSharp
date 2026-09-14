using OSSMDotNet.DeviceControl;

namespace OSSMDotNet.Web.ControlCore.Tests;

internal sealed record BridgeCall(string Kind, long CommandId, DesiredState? Desired)
{
    public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}
