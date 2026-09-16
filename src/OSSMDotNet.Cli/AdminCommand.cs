using System.CommandLine;

namespace OSSMDotNet.Cli;

/// <summary>Defines the local command-line interface for unauthenticated administration.</summary>
public static class AdminCommand
{
    /// <summary>Creates the root command and restricted operator subcommands.</summary>
    public static RootCommand Create()
    {
        var root = new RootCommand("Local OSSM admin client. Start the application with 'aspire run'.");
        var origin = new Option<Uri>("--admin-origin")
        {
            Required = true,
            Recursive = true,
            Description = "Loopback admin origin shown in the Aspire dashboard.",
            CustomParser = result =>
            {
                if (result.Tokens.Count != 1 || !Uri.TryCreate(result.Tokens[0].Value, UriKind.Absolute, out var uri) ||
                    uri.Scheme != "http" || uri.Host != "127.0.0.1" || uri.Port is < 1024 or > 65535 ||
                    uri.UserInfo.Length != 0 || uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0)
                {
                    result.AddError("Provide an HTTP 127.0.0.1 origin on an unprivileged port, without a path, query, fragment, or credentials.");
                    return null;
                }
                return uri;
            }
        };
        root.Options.Add(origin);
        var status = new Command("status", "Show controller status from the local, unauthenticated admin service.");
        status.SetAction(async (parse, cancellationToken) =>
            await AdminClient.SendAsync(parse.GetValue(origin)!, "status", HttpMethod.Get, cancellationToken));
        var stop = new Command("stop", "Stop the controller through the local, unauthenticated admin service.");
        stop.SetAction(async (parse, cancellationToken) =>
            await AdminClient.SendAsync(parse.GetValue(origin)!, "stop", HttpMethod.Post, cancellationToken));
        root.Subcommands.Add(status);
        root.Subcommands.Add(stop);
        return root;
    }
}
