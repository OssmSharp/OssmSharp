namespace OssmSharp.Web.Api;

internal static class Program
{
    internal static Task Main(string[] args) => Task.Run(() => RunAsync(args));

    private static async Task RunAsync(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        await using var app = ApiApplication.Build(builder);
        await app.RunAsync().ConfigureAwait(false);
    }
}
