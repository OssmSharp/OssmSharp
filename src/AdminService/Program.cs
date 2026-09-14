using System.Net;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Options;
using OSSMDotNet.Web.AdminService;
using OSSMDotNet.Web.AdminService.Components;
using OSSMDotNet.Web.HostRuntime;
using OSSMDotNet.Web.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
builder.Services.AddAdminConfiguration().AddLocalBlazor().AddAdminControl();
builder.Services.AddOptions<KestrelServerOptions>()
    .Configure<IOptions<AdminOptions>>((server, options) =>
    {
        server.Listen(IPAddress.Loopback, options.Value.AdminPort);
        server.Limits.MaxRequestBodySize = 16_384;
    });
builder.Services.AddHostedService<DeviceLoop>();
builder.Services.AddHostedService<GuestPipeServer>();

await using var app = builder.Build();
using var ownership = new Mutex(true, "ossm-web-controls-prototype-device", out var ownsDevice);
if (!ownsDevice)
{
    app.Logger.LogCritical("Another admin instance owns the prototype device. Stop it before starting another.");
    return 2;
}
app.UseWebSockets();
app.UseLocalBoundary(app.Services.GetRequiredService<IOptions<AdminOptions>>().Value.AdminPort);
app.UseStaticFiles();
app.UseAntiforgery();
app.MapStaticAssets();
app.MapDefaultEndpoints();
app.MapAdminEndpoints();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode(options => options.ContentSecurityFrameAncestorsPolicy = "'none'");
await app.RunAsync();
return 0;
