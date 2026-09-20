namespace OSSMDotNet.Abstractions;

/// <summary>
/// Describes the connection state to the device.
/// </summary>
public enum DeviceConnectionState
{
    /// <summary>
    /// Establishing its connection.
    /// </summary>
    Connecting,

    /// <summary>
    /// Connection is active.
    /// </summary>
    Connected,

    /// <summary>
    /// Disconnected
    /// </summary>
    Disconnected,

    /// <summary>
    /// The implementation has encountered a connection or protocol fault.
    /// </summary>
    Faulted,
}
