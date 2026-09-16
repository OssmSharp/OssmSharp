namespace OSSMDotNet.AdminService;

internal sealed class OperatorLogWriter(string category, OperatorLogProvider provider) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    /// <summary>Returns whether the writer captures the specified operator-visible log level.</summary>
    public bool IsEnabled(LogLevel logLevel) => logLevel is >= LogLevel.Information and < LogLevel.None;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        // Only explicitly safe application activity belongs in the operator UI, never scopes or exception details.
        if (IsEnabled(logLevel) && category.StartsWith("OSSMDotNet.", StringComparison.Ordinal) &&
            eventId.Name is "OperatorActivity" or "DeviceActivity")
            provider.Append(logLevel, formatter(state, null));
    }
}
