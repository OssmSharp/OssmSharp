namespace OssmSharp.Cli.Models;

/// <summary>
/// Captures the discovered device and its connection and motion state for command output.
/// </summary>
/// <param name="ConnectionState">The most recently reported connection state.</param>
/// <param name="Device">The discovered device information.</param>
/// <param name="State">The most recently reported motion state.</param>
internal sealed record DeviceCommandOutput(
    DeviceConnectionState ConnectionState,
    DeviceInformation Device,
    DeviceState State);
