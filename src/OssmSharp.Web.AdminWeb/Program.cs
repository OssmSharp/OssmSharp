if (OssmSharp.ApplicationVersion.TryWrite(args, Console.Out))
{
    return;
}

var builder = WebApplication.CreateBuilder(args);
await using var app = OssmSharp.Web.AdminWeb.AdminApplication.Build(builder);
await app.RunAsync();
