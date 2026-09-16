using System.Net;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Options;
using OSSMDotNet.GuestWeb.Components;
using OSSMDotNet.ServiceDefaults;
using OSSMDotNet.WebCommon;

namespace OSSMDotNet.GuestWeb;

/// <summary>Configures the restricted guest web application.</summary>
public static class GuestApplication
{
    /// <summary>Builds the guest application and maps its interactive control route.</summary>
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
        app.UseLocalBoundary(app.Services.GetRequiredService<IOptions<GuestOptions>>().Value.GuestPort, RetroWidgetAssets.InlineStyleHashes);
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
