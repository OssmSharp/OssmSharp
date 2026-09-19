namespace OSSMDotNet;

sealed partial class SimulatedDeviceLog
{
    [LoggerMessage(EventId = 100, EventName = "SimulatorStarted", Level = LogLevel.Information, Message = "Simulator started.")]
    public static partial void LogSimulatorStarted(ILogger logger);

    [LoggerMessage(EventId = 101, EventName = "DeviceConnectionLost", Level = LogLevel.Error, Message = "Device connection lost; feedback is unavailable.")]
    public static partial void LogDeviceConnectionLost(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 102, EventName = "DeviceMotionState", Level = LogLevel.Information, Message = "Device {MotionState}.")]
    public static partial void LogDeviceMotionState(ILogger logger, string motionState);

    [LoggerMessage(EventId = 103, EventName = "DeviceSettingsUpdated", Level = LogLevel.Information, Message = "Device settings updated.")]
    public static partial void LogDeviceSettingsUpdated(ILogger logger);

    [LoggerMessage(EventId = 104, EventName = "DeviceDiagnosticsAvailable", Level = LogLevel.Warning, Message = "Device diagnostics are available.")]
    public static partial void LogDeviceDiagnosticsAvailable(ILogger logger);

    [LoggerMessage(EventId = 105, EventName = "DeviceDiagnosticsDisconnected", Level = LogLevel.Error, Message = "Device diagnostics disconnected.")]
    public static partial void LogDeviceDiagnosticsDisconnected(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 106, EventName = "SimulatorShutdownTimedOut", Level = LogLevel.Warning,
        Message = "Simulator shutdown timed out; stopping it.")]
    public static partial void LogSimulatorShutdownTimedOut(ILogger logger);

    [LoggerMessage(EventId = 107, EventName = "SimulatorStopped", Level = LogLevel.Information, Message = "Simulator stopped.")]
    public static partial void LogSimulatorStopped(ILogger logger);
}
