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
        builder.AddFileLogging();
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
