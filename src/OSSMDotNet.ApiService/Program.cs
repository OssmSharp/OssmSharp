using Microsoft.Extensions.Options;
using OSSMDotNet.ApiService;
using OSSMDotNet.ControlService.Models;
using OSSMDotNet.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
builder.Services
    .AddApiConfiguration()
    .AddApiListener()
    .AddApiComposition()
    .AddApiHostFiltering()
    .AddApiAuthentication()
    .AddApiAuthorization()
    .AddApiHub();

using var ownership = DeviceOwnershipLock.TryAcquire();
await using var app = builder.Build();
if (ownership is null)
{
    ApiLog.AuthorityAlreadyOwned(app.Logger);
    return 2;
}

var controlOptions = app.Services.GetRequiredService<IOptions<ControlServiceOptions>>().Value;
app.UseApiBoundary(controlOptions);
app.UseHostFiltering();
app.UseWebSockets();
app.UseAuthentication();
app.UseAuthorization();
app.MapDefaultEndpoints();
app.MapControlHubV1();
await app.RunAsync();
return 0;
