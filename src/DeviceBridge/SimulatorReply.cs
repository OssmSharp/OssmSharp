using OSSMDotNet.DeviceControl;

namespace OSSMDotNet.Simulation;

internal sealed record SimulatorReply(int V, long? Id, bool? Ok, string? Error, string? Event, DeviceSample? State);
