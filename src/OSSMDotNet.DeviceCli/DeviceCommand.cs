using System.CommandLine;
using System.CommandLine.Parsing;
using System.Globalization;

namespace OSSMDotNet.DeviceCli;

/// <summary>
/// Defines commands for exercising a local OSSM simulator without the control service.
/// </summary>
public static class DeviceCommand
{
    /// <summary>
    /// Creates the root command for the current user's persistent simulator host.
    /// </summary>
    /// <returns>The configured device diagnostic command.</returns>
    public static RootCommand Create() => Create(DeviceCommandRunner.RunAsync);

    /// <summary>
    /// Invokes the device diagnostic command with the supplied arguments.
    /// </summary>
    /// <param name="args">The command-line arguments to parse and execute.</param>
    /// <returns>The command exit code, or 2 for invalid arguments.</returns>
    internal static Task<int> InvokeAsync(string[] args) => InvokeAsync(
        args,
        DeviceCommandRunner.RunAsync,
        CancellationToken.None);

    /// <summary>
    /// Invokes the device diagnostic command with a supplied runner.
    /// </summary>
    /// <param name="args">The command-line arguments to parse and execute.</param>
    /// <param name="runAsync">The operation runner selected by the parsed command.</param>
    /// <returns>The command exit code, or 2 for invalid arguments.</returns>
    internal static async Task<int> InvokeAsync(
        string[] args,
        Func<DeviceCommandRequest, CancellationToken, Task<int>> runAsync,
        CancellationToken cancellationToken)
    {
        var parse = Create(runAsync).Parse(args);
        var exitCode = await parse.InvokeAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        return parse.Errors.Count == 0 ? exitCode : 2;
    }

    /// <summary>
    /// Creates the root device diagnostic command with a supplied operation runner.
    /// </summary>
    /// <param name="runAsync">The operation runner selected by each command.</param>
    /// <returns>The configured root command.</returns>
    internal static RootCommand Create(
        Func<DeviceCommandRequest, CancellationToken, Task<int>> runAsync)
    {
        ArgumentNullException.ThrowIfNull(runAsync);

        var root = new RootCommand("Manage one persistent local OSSM simulator for device diagnostics.");
        var simulatorPath = new Option<string>("--simulator-path")
        {
            Description = "Absolute path to the ossm-sim-bridge executable.",
            Required = true,
            CustomParser = result =>
            {
                if (result.Tokens.Count != 1)
                {
                    result.AddError("Provide one absolute simulator executable path.");
                    return null;
                }

                var path = result.Tokens[0].Value;
                if (!Path.IsPathFullyQualified(path) || !File.Exists(path))
                {
                    result.AddError("Provide an absolute path to an existing simulator executable.");
                    return null;
                }

                return Path.GetFullPath(path);
            },
        };
        var start = new Command("start", "Start or reuse the persistent local simulator host.");
        start.Options.Add(simulatorPath);
        start.SetAction((parse, cancellationToken) => runAsync(
            new DeviceCommandRequest(
                DeviceCommandOperation.Start,
                parse.GetValue(simulatorPath)!),
            cancellationToken));

        var status = new Command("status", "Print the running simulator's latest device state as JSON.");
        status.SetAction((parse, cancellationToken) => runAsync(
            new DeviceCommandRequest(DeviceCommandOperation.Status),
            cancellationToken));

        var speed = PercentageOption("--speed", "Motion speed percentage from 1 to 100.", 1);
        var stroke = PercentageOption("--stroke", "Stroke length percentage from 1 to 100.", 1, 20);
        var depth = PercentageOption("--depth", "Maximum depth percentage from 1 to 100.", 1, 50);
        var seconds = new Option<double>("--seconds")
        {
            Description = "Motion duration in seconds from 0.1 through 60.",
            DefaultValueFactory = _ => 3,
            CustomParser = result => ParseDouble(result, 0.1, 60),
        };
        var move = new Command("move", "Run one bounded Simple Stroke motion, then settle at zero speed.");
        move.Options.Add(speed);
        move.Options.Add(stroke);
        move.Options.Add(depth);
        move.Options.Add(seconds);
        move.Validators.Add(result =>
        {
            var strokeValue = ParseRawOrDefault(result.GetResult(stroke), 20);
            var depthValue = ParseRawOrDefault(result.GetResult(depth), 50);
            if (strokeValue is null || depthValue is null)
            {
                return;
            }

            if (strokeValue > depthValue)
            {
                result.AddError("Stroke must not exceed depth.");
            }
        });
        move.SetAction((parse, cancellationToken) => runAsync(
            new DeviceCommandRequest(
                DeviceCommandOperation.Move,
                Speed: parse.GetValue(speed),
                Stroke: parse.GetValue(stroke),
                Depth: parse.GetValue(depth),
                Duration: TimeSpan.FromSeconds(parse.GetValue(seconds))),
            cancellationToken));

        var stop = new Command("stop", "Set zero speed and stop the persistent simulator host.");
        stop.SetAction((parse, cancellationToken) => runAsync(
            new DeviceCommandRequest(DeviceCommandOperation.Stop),
            cancellationToken));

        root.Subcommands.Add(start);
        root.Subcommands.Add(status);
        root.Subcommands.Add(move);
        root.Subcommands.Add(stop);
        return root;
    }

    private static Option<int> PercentageOption(
        string name,
        string description,
        int minimum,
        int? defaultValue = null)
    {
        var option = new Option<int>(name)
        {
            Description = description,
            Required = defaultValue is null,
            CustomParser = result => ParseInt32(result, minimum, 100),
        };
        if (defaultValue is { } value)
        {
            option.DefaultValueFactory = _ => value;
        }

        return option;
    }

    private static int ParseInt32(ArgumentResult result, int minimum, int maximum)
    {
        if (result.Tokens.Count == 1 &&
            int.TryParse(result.Tokens[0].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var value) &&
            value >= minimum && value <= maximum)
        {
            return value;
        }

        result.AddError($"Provide an integer from {minimum} through {maximum}.");
        return default;
    }

    private static double ParseDouble(ArgumentResult result, double minimum, double maximum)
    {
        if (result.Tokens.Count == 1 &&
            double.TryParse(result.Tokens[0].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value) &&
            value >= minimum && value <= maximum)
        {
            return value;
        }

        result.AddError($"Provide a number from {minimum} through {maximum}.");
        return default;
    }

    private static int? ParseRawOrDefault(OptionResult? result, int defaultValue)
    {
        if (result is null || result.Tokens.Count == 0)
        {
            return defaultValue;
        }

        return result.Tokens.Count == 1 &&
            int.TryParse(result.Tokens[0].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }
}
