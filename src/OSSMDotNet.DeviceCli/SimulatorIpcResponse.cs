namespace OSSMDotNet.DeviceCli;

/// <summary>
/// Represents the outcome of one private simulator-host operation.
/// </summary>
internal sealed record SimulatorIpcResponse(
    bool Ok,
    DeviceCommandOutput? Output = null,
    string? Error = null);
