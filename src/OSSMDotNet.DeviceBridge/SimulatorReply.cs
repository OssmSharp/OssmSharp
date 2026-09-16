using OSSMDotNet.Models;

namespace OSSMDotNet.DeviceBridge;

internal sealed record SimulatorReply(int V, long? Id, bool? Ok, string? Error, string? Event, DeviceSample? State);
