namespace OssmSharp.Runtime.Oem;

/// <summary>
/// Describes request completion without claiming rollback.
/// </summary>
/// <param name="Status">Why execution finished.</param>
/// <param name="CommandsWritten">The number of primitive writes that completed successfully.</param>
public sealed record SetResult(SetStatus Status, int CommandsWritten);
