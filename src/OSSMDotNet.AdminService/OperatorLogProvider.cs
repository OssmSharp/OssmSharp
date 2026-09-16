namespace OSSMDotNet.AdminService;

/// <summary>Retains a bounded, redacted activity log for the local operator UI.</summary>
public sealed class OperatorLogProvider(TimeProvider time) : ILoggerProvider
{
    private const int Capacity = 200;
    private readonly object _gate = new();
    private readonly Queue<OperatorLogEntry> _entries = new(Capacity);
    private long _sequence;
    private bool _disposed;

    /// <summary>Creates a logger that writes eligible activity entries to this provider.</summary>
    public ILogger CreateLogger(string categoryName) => new OperatorLogWriter(categoryName, this);

    /// <summary>Returns retained entries after a sequence number.</summary>
    public IReadOnlyList<OperatorLogEntry> ReadAfter(long sequence)
    {
        lock (_gate)
        {
            if (_disposed || sequence >= _sequence) return [];
            return _entries.Where(entry => entry.Sequence > sequence).ToArray();
        }
    }

    internal void Append(LogLevel level, string message)
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (_entries.Count == Capacity) _entries.Dequeue();
            _entries.Enqueue(new(++_sequence, time.GetUtcNow(), level,
                message.Length <= 400 ? message : message[..400] + "..."));
        }
    }

    /// <summary>Clears retained entries and stops accepting new activity.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _entries.Clear();
        }
    }
}
