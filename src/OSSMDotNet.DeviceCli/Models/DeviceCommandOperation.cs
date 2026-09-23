namespace OSSMDotNet.DeviceCli.Models;

/// <summary>
/// Identifies the operation requested from the persistent simulator host.
/// </summary>
internal enum DeviceCommandOperation
{
    /// <summary>
    /// Starts the local simulator host.
    /// </summary>
    Start,
    /// <summary>
    /// Reads the simulator's current device status.
    /// </summary>
    Status,
    /// <summary>
    /// Submits a bounded simulator movement.
    /// </summary>
    Move,
    /// <summary>
    /// Stops simulator movement.
    /// </summary>
    Stop,
}
