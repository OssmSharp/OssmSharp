namespace OSSMDotNet.DeviceCli;

/// <summary>
/// Carries the parsed device diagnostic command and its bounded motion settings.
/// </summary>
internal sealed record DeviceCommandRequest(
    DeviceCommandOperation Operation,
    string? SimulatorPath = null,
    int Speed = 0,
    int Stroke = 20,
    int Depth = 50,
    TimeSpan Duration = default);
