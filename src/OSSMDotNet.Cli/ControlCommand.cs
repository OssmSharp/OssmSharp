using System.CommandLine;

namespace OSSMDotNet.Cli;

/// <summary>
/// Defines the direct command-line client for the authenticated control service.
/// </summary>
public static class ControlCommand
{
    /// <summary>
    /// Creates the status and stop commands for a loopback HTTP or remote HTTPS control service.
    /// </summary>
    /// <returns>The root command with a loopback default and an optional path-free control-service endpoint.</returns>
    public static RootCommand Create()
    {
        var root = new RootCommand("OSSM control CLI. Configure OSSM_CONTROL_CLIENT_SERVICE_KEY before connecting.");
        var endpoint = new Option<Uri>("--endpoint")
        {
            Recursive = true,
            Description = "Control-service origin (defaults to http://127.0.0.1/). Use loopback HTTP for local development or HTTPS for a remote service.",
            DefaultValueFactory = _ => new Uri("http://127.0.0.1/"),
            CustomParser = result =>
            {
                if (result.Tokens.Count != 1 || !Uri.TryCreate(result.Tokens[0].Value, UriKind.Absolute, out var uri) ||
                    uri.UserInfo.Length != 0 || uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
                    (uri.IsLoopback ? uri.Scheme != "http" : uri.Scheme != "https") || uri.Port is < 1 or > 65535)
                {
                    result.AddError("Provide a path-free loopback HTTP or remote HTTPS control-service origin without credentials.");
                    return null;
                }
                return uri;
            }
        };
        root.Options.Add(endpoint);
        var status = new Command("status", "Show the latest authorized control-service state.");
        status.SetAction(async (parse, cancellationToken) =>
            await ControlServiceClient.SendAsync(parse.GetValue(endpoint)!, "status", cancellationToken));
        var stop = new Command("stop", "Request a control-service Stop without treating acceptance as device verification.");
        stop.SetAction(async (parse, cancellationToken) =>
            await ControlServiceClient.SendAsync(parse.GetValue(endpoint)!, "stop", cancellationToken));
        root.Subcommands.Add(status);
        root.Subcommands.Add(stop);
        return root;
    }
}
