namespace OSSMDotNet.Models;

/// <summary>Represents one bounded, redacted activity entry for a trusted operator.</summary>
public sealed record ControlActivityEntry(long Sequence, DateTimeOffset Timestamp, string Level, string Message);
