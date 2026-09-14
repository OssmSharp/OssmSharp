using System.CommandLine;

namespace OSSMDotNet.Web.PrototypeCli;

public static class AdminCommand
{
    public static RootCommand Create()
    {
        var root = new RootCommand("Private OSSM admin client. Use 'aspire run' to start the application.");
        var origin = new Option<Uri>("--admin-origin")
        {
            Required = true,
            Recursive = true,
            Description = "Admin loopback origin from the Aspire dashboard, without its credential fragment.",
            CustomParser = result =>
            {
                if (result.Tokens.Count != 1 || !Uri.TryCreate(result.Tokens[0].Value, UriKind.Absolute, out var uri) ||
                    uri.Scheme != "http" || uri.Host != "127.0.0.1" || uri.Port is < 1024 or > 65535 ||
                    uri.UserInfo.Length != 0 || uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0)
                {
                    result.AddError("Supply an HTTP 127.0.0.1 origin with an unprivileged port and no path, query or credential.");
                    return null;
                }
                return uri;
            }
        };
        root.Options.Add(origin);
        var status = new Command("status", "Read controller status. Supply the operator capability on stdin.");
        status.SetAction(async (parse, cancellationToken) =>
            await AdminClient.SendAsync(parse.GetValue(origin)!, "status", HttpMethod.Get, cancellationToken));
        var stop = new Command("stop", "Request a serialized Stop. Supply the operator capability on stdin.");
        stop.SetAction(async (parse, cancellationToken) =>
            await AdminClient.SendAsync(parse.GetValue(origin)!, "stop", HttpMethod.Post, cancellationToken));
        root.Subcommands.Add(status);
        root.Subcommands.Add(stop);
        return root;
    }
}
