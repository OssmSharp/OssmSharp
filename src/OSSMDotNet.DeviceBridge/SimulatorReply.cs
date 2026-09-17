using OSSMDotNet.Models;

namespace OSSMDotNet.DeviceBridge;

internal readonly record struct SimulatorReply(int V, long? Id, bool? Ok, string? Error, string? Event, DeviceSample? State);
