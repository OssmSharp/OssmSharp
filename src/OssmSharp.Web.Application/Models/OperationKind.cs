namespace OssmSharp.Web.Application.Models;

/// <summary>
/// Distinguishes stop, zero-speed envelope, and desired-motion device writes.
/// </summary>
internal enum OperationKind
{
    /// <summary>
    /// Sends a safety stop to the device.
    /// </summary>
    Stop,
    /// <summary>
    /// Sends zero speed as part of the motion envelope.
    /// </summary>
    Zero,
    /// <summary>
    /// Sends the desired motion settings.
    /// </summary>
    Motion
}
