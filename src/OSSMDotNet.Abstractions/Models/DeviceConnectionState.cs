namespace OSSMDotNet.Abstractions.Models;

/// <summary>
/// Describes the connection state to the device.
/// </summary>
public enum DeviceConnectionState
{
    /// <summary>
    /// Disconnected
    /// </summary>
    Disconnected,

    /// <summary>
    /// Establishing its connection.
    /// </summary>
    Connecting,

    /// <summary>
    /// Connection is active.
    /// </summary>
    Connected,
}
