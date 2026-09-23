namespace OSSMDotNet.DeviceCli.Simulator.Ipc;

/// <summary>
/// Represents the outcome of one private simulator-host operation.
/// </summary>
/// <param name="Ok">Whether the operation succeeded.</param>
/// <param name="Output">The device status returned on success, if any.</param>
/// <param name="Error">The failure description, if the operation failed.</param>
internal sealed record SimulatorIpcResponse(
    bool Ok,
    DeviceCommandOutput? Output = null,
    string? Error = null);
