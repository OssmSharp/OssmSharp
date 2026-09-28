namespace OssmSharp.Device.Runtime;

/// <summary>
/// Describes submission completion without claiming rollback or physical motion confirmation.
/// </summary>
/// <param name="Outcome">Why execution finished.</param>
/// <param name="CommandsWritten">The number of primitive writes that completed successfully.</param>
public sealed record SettingsResult(SettingsOutcome Outcome, int CommandsWritten);
