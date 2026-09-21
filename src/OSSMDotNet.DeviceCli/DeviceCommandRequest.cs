namespace OSSMDotNet.DeviceCli;

internal sealed record DeviceCommandRequest(
    DeviceCommandOperation Operation,
    string SimulatorPath,
    int Speed = 0,
    int Stroke = 20,
    int Depth = 50,
    TimeSpan Duration = default);
