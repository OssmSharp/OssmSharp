namespace OSSMDotNet.DeviceBridge.Models;

internal readonly record struct SimulatorReply(int V, long? Id, bool? Ok, string? Error, string? Event, DeviceState? State);
