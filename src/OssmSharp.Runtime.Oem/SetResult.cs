namespace OssmSharp.Runtime.Oem;

/// <summary>
/// Describes submission completion without claiming rollback or physical motion confirmation.
/// </summary>
/// <param name="Status">Why execution finished.</param>
/// <param name="CommandsWritten">The number of primitive writes that completed successfully.</param>
public sealed record SetResult(SetStatus Status, int CommandsWritten);
