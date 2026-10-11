using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using OssmSharp.Web.Models;
using OssmSharp.Web.ServiceDefaults;

namespace OssmSharp.Web;

internal static class Program
{
    // Native Bluetooth callbacks must not depend on a console application's macOS main thread.
    private static Task<int> Main(string[] args) => Task.Run(async () =>
    {
        if (ApplicationVersion.TryWrite(args, Console.Out))
        {
            return 0;
        }

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = args,
            ContentRootPath =
#if MACOS
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Resources")),
#else
                AppContext.BaseDirectory,
#endif
        });
        string? defaultLogDirectory = null;
        if (builder.Configuration["OSSM:FileLogging:Directory"] is null)
        {
#if MACOS
            // Native startup changes the current directory to the bundle before Main.
            var launchDirectory = ObjCRuntime.Runtime.OriginalWorkingDirectory;
            if (string.IsNullOrEmpty(launchDirectory))
                throw new InvalidOperationException(
                    "The launch folder is unknown. Configure OSSM:FileLogging:Directory.");
#else
            var launchDirectory = Environment.CurrentDirectory;
#endif
            defaultLogDirectory = Path.Combine(launchDirectory, "logs");
        }
        builder.AddFileLogging(defaultLogDirectory);
        builder.Services.AddOptions<WebOptions>()
            .BindConfiguration("OSSM:Web")
            .ValidateOnStart();
        builder.Services.AddSingleton<IValidateOptions<WebOptions>, WebOptionsValidator>();
        builder.Services.AddSingleton<IWebApplicationFactory, WebApplicationFactory>();
        builder.Services.AddHostedService<WebApplications>();
        using var host = builder.Build();
        var applications = host.Services.GetServices<IHostedService>().OfType<WebApplications>().Single();
        await host.RunAsync().ConfigureAwait(false);
        return applications.ExecuteTask?.IsFaulted == true ? 1 : 0;
    });
}
