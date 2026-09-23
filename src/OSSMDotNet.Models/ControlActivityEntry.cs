namespace OSSMDotNet.Models;

/// <summary>Represents one bounded, redacted activity entry for a trusted operator.</summary>
/// <summary>
/// Describes one sequenced operator activity event.
/// </summary>
/// <param name="Sequence">The ordering sequence assigned to the event.</param>
/// <param name="Timestamp">The time the event occurred.</param>
/// <param name="Level">The event's severity level.</param>
/// <param name="Message">The redacted event description.</param>
public sealed record ControlActivityEntry(long Sequence, DateTimeOffset Timestamp, string Level, string Message);
