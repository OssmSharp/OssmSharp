namespace OSSMDotNet.ControlCore.Tests.Helpers;

/// <summary>
/// Captures a device write whose completion a test can control.
/// </summary>
/// <param name="Kind">The kind of device write.</param>
/// <param name="Desired">The settings sent to the bridge.</param>
internal sealed record BridgeCall(string Kind, MotionSettings Desired)
{
    /// <summary>
    /// Gets the completion signal for the pending write.
    /// </summary>
    public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}
