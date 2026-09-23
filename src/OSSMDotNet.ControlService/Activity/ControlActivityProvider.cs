using Microsoft.Extensions.Logging;
using OSSMDotNet.Models;

namespace OSSMDotNet.ControlService.Activity;

/// <summary>Retains a bounded, redacted activity log for trusted operator consumers.</summary>
/// <param name="time">The clock used to timestamp retained activity.</param>
public sealed class ControlActivityProvider(TimeProvider time) : ILoggerProvider
{
    private const int Capacity = 200;
    private readonly object _gate = new();
    private readonly Queue<ControlActivityEntry> _entries = new(Capacity);
    private long _sequence;
    private bool _disposed;

    /// <summary>Creates a logger that writes eligible activity entries to this provider.</summary>
    /// <param name="categoryName">The category checked before an event is retained.</param>
    /// <returns>A logger scoped to the supplied category.</returns>
    public ILogger CreateLogger(string categoryName) => new ControlActivityWriter(categoryName, this);

    /// <summary>Returns retained entries after a sequence number.</summary>
    /// <param name="sequence">The last sequence already seen by the caller.</param>
    /// <returns>The retained entries after that sequence, or an empty list after disposal.</returns>
    public IReadOnlyList<ControlActivityEntry> ReadAfter(long sequence)
    {
        lock (_gate)
        {
            if (_disposed || sequence >= _sequence)
                return [];
            return _entries.Where(entry => entry.Sequence > sequence).ToArray();
        }
    }

    /// <summary>
    /// Returns the next ordered activity page within the caller's serialized byte limit.
    /// </summary>
    /// <param name="sequence">The last sequence already seen by the operator.</param>
    /// <param name="measureBytes">Measures the serialized size of a candidate page.</param>
    /// <param name="maximumBytes">The maximum serialized page size in bytes.</param>
    /// <returns>A bounded page with gap and continuation information.</returns>
    /// <exception cref="InvalidOperationException">A single retained entry cannot fit the requested limit.</exception>
    public ControlActivityBatch ReadBatchAfter(long sequence, Func<ControlActivityBatch, int> measureBytes, int maximumBytes)
    {
        ArgumentNullException.ThrowIfNull(measureBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        ControlActivityEntry[] entries;
        long latest;
        long retainedFirst;
        lock (_gate)
        {
            latest = _sequence;
            retainedFirst = !_disposed && _entries.TryPeek(out var first) ? first.Sequence : latest + 1;
            if (_disposed || sequence >= latest)
                return new(retainedFirst, latest, 0, 0, null, sequence < retainedFirst - 1, []);
            entries = _entries.ToArray();
        }

        var gap = sequence < retainedFirst - 1;
        var cursor = Math.Max(sequence, retainedFirst - 1);
        var remaining = entries.Where(entry => entry.Sequence > cursor).ToArray();
        if (remaining.Length == 0)
            return new(retainedFirst, latest, 0, 0, null, gap, []);

        var page = new List<ControlActivityEntry>(remaining.Length);
        for (var index = 0; index < remaining.Length; index++)
        {
            page.Add(remaining[index]);
            var hasMore = index + 1 < remaining.Length;
            var candidate = CreateBatch(retainedFirst, latest, page, hasMore, gap);
            if (measureBytes(candidate) <= maximumBytes)
                continue;

            page.RemoveAt(page.Count - 1);
            break;
        }

        // Entries are capped when appended, so even a maximally escaped single entry always fits.
        if (page.Count == 0)
            throw new InvalidOperationException("A bounded activity entry exceeded the hub callback limit.");

        return CreateBatch(retainedFirst, latest, page, page.Count < remaining.Length, gap);
    }

    /// <summary>
    /// Appends a bounded activity entry, dropping the oldest when retention is full.
    /// </summary>
    /// <param name="level">The severity reported to the operator.</param>
    /// <param name="message">The operator-safe message, truncated to the retention limit.</param>
    internal void Append(LogLevel level, string message)
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            if (_entries.Count == Capacity)
                _entries.Dequeue();
            _entries.Enqueue(new(++_sequence, time.GetUtcNow(), level.ToString(),
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

    private static ControlActivityBatch CreateBatch(long retainedFirst, long retainedLast,
        IReadOnlyList<ControlActivityEntry> entries, bool hasMore, bool gap) =>
        new(retainedFirst, retainedLast, entries[0].Sequence, entries[^1].Sequence,
            hasMore ? entries[^1].Sequence : null, gap, entries.ToArray());

}
