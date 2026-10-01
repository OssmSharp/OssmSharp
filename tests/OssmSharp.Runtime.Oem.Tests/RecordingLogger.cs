using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace OssmSharp.Runtime.Oem.Tests;

internal sealed class RecordingLogger<TCategory> : ILogger<TCategory>
{
    public ConcurrentQueue<(EventId Id, LogLevel Level, Exception? Exception, string Message, IReadOnlyList<KeyValuePair<string, object?>> StructuredState)> Entries { get; } = new();

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => Entries.Enqueue((eventId, logLevel, exception, formatter(state, exception), ((IEnumerable<KeyValuePair<string, object?>>)state!).ToArray()));
}
