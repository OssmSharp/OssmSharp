using OSSMDotNet.Abstractions;

namespace OSSMDotNet.DeviceCli;

internal sealed record DeviceCommandOutput(
    DeviceConnectionState ConnectionState,
    DeviceInformation Device,
    DeviceState State);
