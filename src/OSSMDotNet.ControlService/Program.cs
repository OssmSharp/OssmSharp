using Microsoft.Extensions.Options;
using OSSMDotNet.ControlService;
using OSSMDotNet.ControlService.Models;
using OSSMDotNet.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
builder.Services
    .AddControlServiceConfiguration()
    .AddControlServiceListener()
    .AddControlServiceAuthority()
    .AddControlServiceHostFiltering()
    .AddControlServiceAuthentication()
    .AddControlServiceAuthorization()
    .AddControlServiceHub();

await using var app = builder.Build();
using var ownership = DeviceOwnershipLock.TryAcquire();
if (ownership is null)
{
    ControlServiceLog.AuthorityAlreadyOwned(app.Logger);
    return 2;
}

var controlOptions = app.Services.GetRequiredService<IOptions<ControlServiceOptions>>().Value;
app.UseControlServiceBoundary(controlOptions);
app.UseHostFiltering();
app.UseWebSockets();
app.UseAuthentication();
app.UseAuthorization();
app.MapDefaultEndpoints();
app.MapControlHubV1();
await app.RunAsync();
return 0;
