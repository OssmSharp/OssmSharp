using System.Net;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Options;
using OSSMDotNet.Web.GuestWeb.Components;
using OSSMDotNet.Web.HostRuntime;
using OSSMDotNet.Web.ServiceDefaults;

namespace OSSMDotNet.Web.GuestWeb;

public static class GuestApplication
{
    public static WebApplication Build(WebApplicationBuilder builder)
    {
        builder.AddServiceDefaults();
        builder.Services.AddGuestConfiguration().AddLocalBlazor().AddGuestSession();
        builder.Services.AddOptions<KestrelServerOptions>()
            .Configure<IOptions<GuestOptions>>((server, options) =>
            {
                server.Listen(IPAddress.Loopback, options.Value.GuestPort);
                server.Limits.MaxRequestBodySize = 16_384;
            });
        builder.WebHost.UseStaticWebAssets();

        var app = builder.Build();
        app.UseWebSockets();
        app.UseLocalBoundary(app.Services.GetRequiredService<IOptions<GuestOptions>>().Value.GuestPort);
        app.UseStaticFiles();
        app.UseAntiforgery();
        app.MapStaticAssets();
        app.MapDefaultEndpoints();
        app.MapRazorComponents<App>().AddInteractiveServerRenderMode(options =>
        {
            options.ContentSecurityFrameAncestorsPolicy = "'none'";
        });
        return app;
    }
}
