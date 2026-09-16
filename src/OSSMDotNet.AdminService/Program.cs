using Microsoft.Extensions.Options;
using OSSMDotNet.AdminService;
using OSSMDotNet.AdminService.Components;
using OSSMDotNet.ServiceDefaults;
using OSSMDotNet.WebCommon;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
builder.Services.AddAdminConfiguration().AddLocalBlazor().AddAdminControl().AddAdminListener();
builder.Services.AddHostedService<DeviceLoop>();
builder.Services.AddHostedService<GuestPipeServer>();

await using var app = builder.Build();
using var ownership = new Mutex(true, "ossm-web-controls-prototype-device", out var ownsDevice);
if (!ownsDevice)
{
    app.Logger.LogCritical("Another admin service already controls the prototype device. Stop that service before starting this one.");
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
