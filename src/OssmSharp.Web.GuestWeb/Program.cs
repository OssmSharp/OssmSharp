namespace OssmSharp.Web.GuestWeb;

/// <summary>
/// Starts the restricted guest web application.
/// </summary>
public static class Program
{
    /// <summary>
    /// Prints the build version or builds and runs the guest application.
    /// </summary>
    /// <param name="args">Startup arguments, or <c>--version</c> to print the version without starting services.</param>
    /// <returns>A task that completes after version output or application shutdown.</returns>
    public static async Task Main(string[] args)
    {
        if (ApplicationVersion.TryWrite(args, Console.Out))
        {
            return;
        }

        await using var app = GuestApplication.Build(WebApplication.CreateBuilder(args));
        await app.RunAsync();
    }
}
