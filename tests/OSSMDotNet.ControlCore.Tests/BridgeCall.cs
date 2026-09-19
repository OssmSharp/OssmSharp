using OSSMDotNet.Models;

namespace OSSMDotNet.ControlCore.Tests;

internal sealed record BridgeCall(string Kind, long CommandId, MotionSettings? Desired)
{
    public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}
