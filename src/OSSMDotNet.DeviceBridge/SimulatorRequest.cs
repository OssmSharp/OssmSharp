using OSSMDotNet.Models;

namespace OSSMDotNet.DeviceBridge;

internal sealed record SimulatorRequest(int V, long Id, string Op, long? CommandId, DesiredState? Desired, int? Value);
