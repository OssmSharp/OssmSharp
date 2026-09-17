using Microsoft.Extensions.Options;
using OSSMDotNet.AdminWeb.Components;
using OSSMDotNet.ControlClient;
using OSSMDotNet.ServiceDefaults;
using OSSMDotNet.WebCommon;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
builder.Services.AddAdminConfiguration().AddLocalBlazor().AddControlClient("OSSM:ControlClient").AddAdminListener();

await using var app = builder.Build();
app.UseLocalBoundary(app.Services.GetRequiredService<IOptions<AdminOptions>>().Value.AdminPort, RetroWidgetAssets.InlineStyleHashes);
app.UseStaticFiles();
app.UseAntiforgery();
app.MapStaticAssets();
app.MapDefaultEndpoints();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode(options => options.ContentSecurityFrameAncestorsPolicy = "'none'");
await app.RunAsync();
