using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using OssmSharp.Web.Api;
using OssmSharp.Web.Application.Models;
using OssmSharp.Web.ServiceDefaults;

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

await using var app = builder.Build();

var controlOptions = app.Services.GetRequiredService<IOptions<ControlServiceOptions>>().Value;
app.UseApiBoundary(controlOptions);
app.UseHostFiltering();
app.UseWebSockets();
app.UseAuthentication();
app.UseAuthorization();
app.MapDefaultEndpoints();
app.MapControlHubV1();
await app.StartAsync();
await app.WaitForShutdownAsync();
return 0;
