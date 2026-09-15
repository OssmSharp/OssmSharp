using Microsoft.Extensions.Options;
using OSSMDotNet.Web.AdminService;
using OSSMDotNet.Web.AdminService.Components;
using OSSMDotNet.Web.HostRuntime;
using OSSMDotNet.Web.ServiceDefaults;
using OSSMDotNet.Web.SharedUi;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
builder.Services.AddAdminConfiguration().AddLocalBlazor().AddAdminControl().AddAdminListener();
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
app.UseLocalBoundary(app.Services.GetRequiredService<IOptions<AdminOptions>>().Value.AdminPort, RetroWidgetAssets.InlineStyleHashes);
app.UseStaticFiles();
app.UseAntiforgery();
app.MapStaticAssets();
app.MapDefaultEndpoints();
app.MapAdminEndpoints();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode(options => options.ContentSecurityFrameAncestorsPolicy = "'none'");
await app.RunAsync();
return 0;
