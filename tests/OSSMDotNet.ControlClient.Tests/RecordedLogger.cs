using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace OSSMDotNet.ControlClient.Tests;

internal sealed class RecordedLogger<T> : ILogger<T>
{
    private readonly List<(LogLevel Level, int EventId, string Message, Exception? Exception)> _entries = [];

    internal IReadOnlyList<(LogLevel Level, int EventId, string Message, Exception? Exception)> Entries
    {
        get
        {
            lock (_entries)
                return _entries.ToArray();
        }
    }

    public IDisposable BeginScope<TState>(TState state) where TState : notnull =>
        NullLogger<T>.Instance.BeginScope(state)!;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        lock (_entries)
            _entries.Add((logLevel, eventId.Id, formatter(state, exception), exception));
    }
}
