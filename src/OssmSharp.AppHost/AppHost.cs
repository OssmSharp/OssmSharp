using System.Buffers.Text;
using System.Security.Cryptography;

var builder = DistributedApplication.CreateBuilder(args);

var adminServiceKey = builder.AddParameter("admin-web-service-key", NewToken, secret: true);
var guestServiceKey = builder.AddParameter("guest-web-service-key", NewToken, secret: true);
var deviceIdentifier = builder.AddParameter("device-identifier", () => builder.Configuration["Parameters:device-identifier"] ?? string.Empty);

var controlFramework = OperatingSystem.IsMacOS()
    ? "net10.0-macos27.0"
    : OperatingSystem.IsWindows() ? "net10.0-windows10.0.19041.0" : "net10.0";

var control = builder.AddProject<Projects.OssmSharp_Web_Api>("control")
    .WithArgs("--framework", controlFramework)
    .WithEnvironment("DOTNET_ENVIRONMENT", "Development")
    .WithEnvironment("Logging__LogLevel__OssmSharp.Runtime.Oem", "Debug")
    .WithHttpEndpoint(isProxied: false)
    .WithEndpoint("http", endpoint => endpoint.TargetHost = "127.0.0.1")
    .WithHttpHealthCheck("/health")
    .WithEnvironment("OSSM__Control__AdminWebServiceKey", adminServiceKey)
    .WithEnvironment("OSSM__Control__GuestWebServiceKey", guestServiceKey)
    .WithEnvironment("OSSM__Control__DeviceIdentifier", deviceIdentifier);

var guest = builder.AddProject<Projects.OssmSharp_Web_GuestWeb>("guest")
    .WithEnvironment("DOTNET_ENVIRONMENT", "Development")
    .WithHttpEndpoint(env: "OSSM__Guest__GuestPort", isProxied: false)
    .WithEndpoint("http", endpoint => endpoint.TargetHost = "127.0.0.1")
    .WithHttpHealthCheck("/health")
    .WithEnvironment(
        "OSSM__ControlClient__ControlOrigin",
        ReferenceExpression.Create($"http://127.0.0.1:{control.GetEndpoint("http").Property(EndpointProperty.Port)}"))
    .WithEnvironment("OSSM__ControlClient__ServiceKey", guestServiceKey)
    .WithUrls(context =>
    {
        var origin = $"http://127.0.0.1:{context.GetEndpoint("http")!.Port}";
        context.Urls.Clear();
        context.Urls.Add(new ResourceUrlAnnotation { Url = origin, DisplayText = "Guest (local only)" });
    });

var admin = builder.AddProject<Projects.OssmSharp_Web_AdminWeb>("admin")
    .WithEnvironment("DOTNET_ENVIRONMENT", "Development")
    .WithHttpEndpoint(env: "OSSM__Admin__AdminPort", isProxied: false)
    .WithEndpoint("http", endpoint => endpoint.TargetHost = "127.0.0.1")
    .WithHttpHealthCheck("/health")
    .WithEnvironment(
        "OSSM__ControlClient__ControlOrigin",
        ReferenceExpression.Create($"http://127.0.0.1:{control.GetEndpoint("http").Property(EndpointProperty.Port)}"))
    .WithEnvironment("OSSM__ControlClient__ServiceKey", adminServiceKey)
    .WithEnvironment(
        "OSSM__Admin__GuestOrigin",
        ReferenceExpression.Create($"http://127.0.0.1:{guest.GetEndpoint("http").Property(EndpointProperty.Port)}"))
    .WithUrls(context =>
    {
        var origin = $"http://127.0.0.1:{context.GetEndpoint("http")!.Port}";
        context.Urls.Clear();
        context.Urls.Add(new ResourceUrlAnnotation { Url = origin, DisplayText = "User (local only)" });
    });

guest.WaitFor(control);
admin.WaitFor(control);

await builder.Build().RunAsync();

static string NewToken() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
