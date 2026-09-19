namespace OSSMDotNet.DeviceBridge.Models;

internal sealed record SimulatorRequest(int V, long Id, string Op, long? CommandId, MotionSettings? Desired, int? Value);
