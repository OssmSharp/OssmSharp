namespace OSSMDotNet.AdminService;

/// <summary>Represents one bounded operator-facing activity log entry.</summary>
public sealed record OperatorLogEntry(long Sequence, DateTimeOffset Timestamp, LogLevel Level, string Message);
