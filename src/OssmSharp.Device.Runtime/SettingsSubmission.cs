namespace OssmSharp.Device.Runtime;

internal sealed class SettingsSubmission(MotionSettings settings, CancellationTokenSource pendingCancellation)
{
    public MotionSettings Settings { get; } = settings;

    public CancellationTokenSource PendingCancellation { get; } = pendingCancellation;

    public SettingsOutcome Outcome { get; set; } = SettingsOutcome.Completed;

    public int CommandsWritten { get; set; }

    public SettingsResult Result => new(Outcome, CommandsWritten);
}
