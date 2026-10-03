using OssmSharp.Runtime.Oem;

namespace OssmSharp.Cli;

internal static class CliStrings
{
    internal const string RootHelp = "Control an OSSM device from the command line.";
    internal const string ListHelp = "List nearby devices and their identifiers.";
    internal const string PromptHelp = "Open an interactive device prompt.";
    internal const string DeviceHelp = "Connect to this device identifier. Use list to find identifiers. If omitted, connect to the first nearby device.";
    internal const string RedirectedInput = "Run prompt in a terminal without piping or redirecting stdin.";
    internal const string UsageHint = "Run OssmSharp.Cli --help for usage.";
    internal const string PercentageHelp = "Whole-number percentage from 0 to 100.";
    internal const string SpeedHelp = "Speed (as an integer percentage) from 0 to 100 (50 means 50%). Raw velocity depends on speed and stroke and is rounded down. Zero speed or stroke sends zero raw velocity. This is not physical speed or strokes per minute.";
    internal const string SetHelp = "Set the device's motion.";
    internal const string StopHelp = "Stop the device and drop pending commands.";
    internal const string ResetHelp = "Rearm settings after a disarming failure.";
    internal const string StatusHelp = "Read the device's live state. Speed preserves decimals and values above 100%, and is Unknown at zero stroke.";
    internal const string HelpHelp = "Show command help.";
    internal const string QuitHelp = "Stop the device and exit.";
    internal const string HelpArgument = "Command to describe.";
    internal const string OversizedInput = "Command must be 4096 characters or less. Entire line discarded.";
    internal const string DisarmedSet = "Device is disarmed. Run reset before set.";
    internal const string Cancellation = "Cancelled. Waiting for pending work and cleanup.";
    internal const string SessionEnded = "Session ended. Run OssmSharp.Cli prompt to reconnect.";
    internal const string StopProgress = "Sending stop command...";
    internal const string StopInProgress = "Stop already in progress.";
    internal const string Prompt = "ossm> ";
    internal const string DiscoveryProgress = "Scanning for nearby devices...";
    internal const string NoDevices = "No devices found.";
    internal const string Connected = "Connected. Type 'help' for commands.";

    internal static string Error(string operation, string detail, string failure = "", string reset = "") =>
        $"{failure}ERROR{reset} {operation}: {detail}";

    internal static string MissingOptionValue(string option, string valueName) => $"{option} requires {valueName}.";

    internal static string DiscoveryFailure(string message) => $"{message} Check bluetooth access and that the device is turned on.";

    internal static string DeviceListing(DiscoveredDevice device) => $"{device.Identifier}  {device.Name}";

    internal static string Connecting(string name, string identifier) => $"Connecting to {name} ({identifier})...";

    internal static string PatternHelp(int first, int last, string choices) =>
        FormattableString.Invariant($"Pattern ID {first}..{last} or case-insensitive name: {choices}.");

    internal static string PatternChoice(StrokePattern pattern) => FormattableString.Invariant($"{(int)pattern}={pattern}");

    internal static string InvalidPattern(string value, string choices) => $"Unknown pattern '{value}'. Choose from: {choices}.";

    internal static string InvalidPercentage(string name, string value) => $"{name} '{value}' must be a whole number from 0 to 100.";

    internal static string MissingArguments(string missing, string usage) => $"Missing {missing}; expected {usage}.";

    internal static string SetCompleted(string success, string reset) => $"{success}OK{reset} set: settings sent.";

    internal static string StopCompleted(string success, string reset) => $"{success}OK{reset} stop: stop command sent; pending settings dropped.";

    internal static string ResetCompleted(string success, string reset) => $"{success}OK{reset} reset: ready for settings.";

    internal static string Status(DeviceState state, string success, string reset) =>
        FormattableString.Invariant($"{success}OK{reset} status: pattern={state.Pattern:D} speed={state.Speed?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "Unknown"}{(state.Speed is null ? string.Empty : "%")} stroke={state.Stroke} depth={state.Depth} sensation={state.Sensation}.");

    internal static string SetInterrupted(SetResult result, string warning, string reset) =>
        FormattableString.Invariant($"{warning}{(result.Status == SetStatus.Superseded ? "SUPERSEDED" : "CANCELLED")}{reset} set: {result.CommandsWritten} commands sent; they may still take effect.");

    internal static string OperationCancelled(string operation, string warning, string reset) =>
        $"{warning}CANCELLED{reset} {operation}: pending work cancelled; sent commands may still take effect.";

    internal static string WriteFailure(DeviceWriteException exception) =>
        FormattableString.Invariant($"Could not send settings after {exception.CommandsWritten} completed writes. Settings may be partially applied.");
}
