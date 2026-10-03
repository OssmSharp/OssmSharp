using OssmSharp.Web.Contracts;

namespace OssmSharp.Web.Client.Models;

/// <summary>
/// Provides connection-state checks for client controls.
/// </summary>
public static class DeviceStateExtensions
{
    extension(DeviceStateSnapshot? device)
    {
        /// <summary>
        /// Gets whether a snapshot is connected, not connecting, and has no connection failure; this does not imply readiness.
        /// </summary>
        public bool IsConnectedForControls => device is { IsConnected: true, IsConnecting: false, ConnectionFailed: false };
    }
}