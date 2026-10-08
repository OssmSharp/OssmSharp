using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using OssmSharp.Web.AdminWeb;
using OssmSharp.Web.Api;
using OssmSharp.Web.GuestWeb;
using OssmSharp.Web.Models;
using OssmSharp.Web.ServiceDefaults.Models;

namespace OssmSharp.Web;

/// <summary>
/// Builds isolated application containers with shared trusted connection settings.
/// </summary>
public sealed class WebApplicationFactory(
    IConfiguration configuration,
    IHostEnvironment environment,
    IOptions<WebOptions> options,
    IOptions<FileLoggingOptions> fileLogging) : IWebApplicationFactory
{
    private readonly string serviceKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <inheritdoc />
    public IHost Create(int index)
    {
        var applicationName = index switch
        {
            0 => typeof(ApiApplication).Assembly.GetName().Name!,
            1 => typeof(GuestApplication).Assembly.GetName().Name!,
            2 => typeof(AdminApplication).Assembly.GetName().Name!,
            _ => throw new ArgumentOutOfRangeException(nameof(index)),
        };
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = applicationName,
            EnvironmentName = environment.EnvironmentName,
            ContentRootPath = environment.ContentRootPath,
            WebRootPath = Path.Combine(environment.ContentRootPath, "applications", applicationName, "wwwroot"),
        });
        builder.Configuration.AddConfiguration(configuration);
        if (builder.Configuration.GetSection("Kestrel").Exists()
            || builder.Configuration.GetValue<bool>("preferHostingUrls"))
        {
            throw new InvalidOperationException(
                "Listener overrides are not supported. Configure the three loopback ports under OSSM:Web.");
        }
        var settings = options.Value;
        var apiOrigin = $"http://127.0.0.1:{settings.ApiPort}";
        var guestHostname = configuration["OSSM:Guest:Hostname"];
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OSSM:Control:AdminWebServiceKey"] = index == 0 ? serviceKey : null,
            ["OSSM:ControlClient:ServiceKey"] = index == 2 ? serviceKey : null,
            ["OSSM:ControlClient:EndpointUri"] = apiOrigin,
            ["OSSM:Admin:ListenPort"] = settings.AdminPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["OSSM:Admin:GuestHostname"] = string.IsNullOrEmpty(guestHostname) ? $"http://127.0.0.1:{settings.GuestPort}" : guestHostname,
            ["OSSM:Guest:GuestPort"] = settings.GuestPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["OSSM:FileLogging:Directory"] = Path.Combine(fileLogging.Value.Directory, applicationName),
        });
        // Only the outer host owns console signals; each application still has its own lifetime.
        builder.Services.AddSingleton<IHostLifetime, ApplicationHostLifetime>();
        if (index == 0)
        {
            builder.Services.Configure<KestrelServerOptions>(server => server.Listen(IPAddress.Loopback, settings.ApiPort));
            return ApiApplication.Build(builder);
        }
        var manifest = Path.Combine(environment.ContentRootPath, $"{applicationName}.staticwebassets.endpoints.json");
        return index == 1 ? GuestApplication.Build(builder, manifest) : AdminApplication.Build(builder, manifest);
    }
}
