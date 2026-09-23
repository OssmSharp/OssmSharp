using Microsoft.Extensions.Logging;

namespace OSSMDotNet.ControlService.Activity;

/// <summary>
/// Captures only allowlisted, operator-safe activity from application log events.
/// </summary>
/// <param name="category">The logger category eligible for operator activity.</param>
/// <param name="provider">The bounded activity store receiving eligible events.</param>
internal sealed class ControlActivityWriter(string category, ControlActivityProvider provider) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    /// <summary>Returns whether the writer captures the specified operator-visible log level.</summary>
    public bool IsEnabled(LogLevel logLevel) => logLevel is >= LogLevel.Information and < LogLevel.None;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        // Only explicitly safe application activity belongs in the operator UI, never scopes or exception details.
        if (IsEnabled(logLevel) && category.StartsWith("OSSMDotNet.", StringComparison.Ordinal) &&
            IsSafeActivityEvent(eventId))
            provider.Append(logLevel, formatter(state, null));
    }

    private static bool IsSafeActivityEvent(EventId eventId) =>
        eventId.Name is "OperatorActivityOperation" or "OperatorActivitySafety" or
            "OperatorActivityOwnership" or "ControlServiceDeliveryFailed" or
            "DeviceActivity" or "SimulatorStarted" or
            "DeviceConnectionLost" or "DeviceMotionState" or "DeviceSettingsUpdated" or
            "DeviceDiagnosticsAvailable" or "DeviceDiagnosticsDisconnected" or
            "SimulatorShutdownTimedOut" or "SimulatorStopped";
}
