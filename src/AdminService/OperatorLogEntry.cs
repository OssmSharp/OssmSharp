namespace OSSMDotNet.Web.AdminService;

public sealed record OperatorLogEntry(long Sequence, DateTimeOffset Timestamp, LogLevel Level, string Message);
