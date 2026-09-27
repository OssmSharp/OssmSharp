namespace OssmSharp.Device.Runtime;

/// <summary>The locally owned execution condition, distinct from transport connection state.</summary>
public enum DeviceExecutionState
{
    Disconnected,
    Initializing,
    Ready,
    Paused,
    Locked,
    Faulted,
    Disposed,
}
