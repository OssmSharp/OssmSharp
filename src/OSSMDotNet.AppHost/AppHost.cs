using System.Buffers.Text;
using System.Security.Cryptography;

var builder = DistributedApplication.CreateBuilder(args);
if (!builder.ExecutionContext.IsRunMode)
    throw new InvalidOperationException("This simulator setup supports local Aspire runs only.");

var simulatorDirectory = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", "..", "tools", "ossm-sim-bridge"));
var simulatorPath = Path.Combine(simulatorDirectory, "target", "release",
    OperatingSystem.IsWindows() ? "ossm-sim-bridge.exe" : "ossm-sim-bridge");
var simulatorBuild = builder.AddExecutable("simulator-build", "cargo", simulatorDirectory, "build", "--release", "--locked");

var adminServiceKey = builder.AddParameter("admin-web-service-key", NewToken, secret: true);
var guestServiceKey = builder.AddParameter("guest-web-service-key", NewToken, secret: true);

var control = builder.AddProject<Projects.OssmSharp_Web_Api>("control")
    .WithEnvironment("DOTNET_ENVIRONMENT", "Development")
    .WithHttpEndpoint(env: "OSSM__Control__ControlPort", isProxied: false)
    .WithEndpoint("http", endpoint => endpoint.TargetHost = "127.0.0.1")
    .WithHttpHealthCheck("/health")
    .WithEnvironment("OSSM__Control__BindAddress", "127.0.0.1")
    .WithEnvironment("OSSM__Control__HostName", "127.0.0.1")
    .WithEnvironment("OSSM__Control__AdminWebServiceKey", adminServiceKey)
    .WithEnvironment("OSSM__Control__GuestWebServiceKey", guestServiceKey)
    .WithEnvironment("OSSM__Control__SimulatorPath", simulatorPath)
    .WaitForCompletion(simulatorBuild);

var guest = builder.AddProject<Projects.OssmSharp_Web_GuestWeb>("guest")
    .WithEnvironment("DOTNET_ENVIRONMENT", "Development")
    .WithHttpEndpoint(env: "OSSM__Guest__GuestPort", isProxied: false)
    .WithEndpoint("http", endpoint => endpoint.TargetHost = "127.0.0.1")
    .WithHttpHealthCheck("/health")
    .WithEnvironment("OSSM__ControlClient__ControlOrigin",
        ReferenceExpression.Create($"http://127.0.0.1:{control.GetEndpoint("http").Property(EndpointProperty.Port)}"))
    .WithEnvironment("OSSM__ControlClient__ServiceKey", guestServiceKey)
    .WithUrls(context =>
    {
        var origin = $"http://127.0.0.1:{context.GetEndpoint("http")!.Port}";
        context.Urls.Clear();
        context.Urls.Add(new ResourceUrlAnnotation { Url = origin, DisplayText = "Guest (local only)" });
    });

control.WithEnvironment("OSSM__Control__GuestOrigin",
    ReferenceExpression.Create($"http://127.0.0.1:{guest.GetEndpoint("http").Property(EndpointProperty.Port)}"));

var admin = builder.AddProject<Projects.OssmSharp_Web_AdminWeb>("admin")
    .WithEnvironment("DOTNET_ENVIRONMENT", "Development")
    .WithHttpEndpoint(env: "OSSM__Admin__AdminPort", isProxied: false)
    .WithEndpoint("http", endpoint => endpoint.TargetHost = "127.0.0.1")
    .WithHttpHealthCheck("/health")
    .WithEnvironment("OSSM__ControlClient__ControlOrigin",
        ReferenceExpression.Create($"http://127.0.0.1:{control.GetEndpoint("http").Property(EndpointProperty.Port)}"))
    .WithEnvironment("OSSM__ControlClient__ServiceKey", adminServiceKey)
    .WithUrls(context =>
    {
        var origin = $"http://127.0.0.1:{context.GetEndpoint("http")!.Port}";
        context.Urls.Clear();
        context.Urls.Add(new ResourceUrlAnnotation { Url = origin, DisplayText = "Operator (local only)" });
    });

guest.WaitFor(control);
admin.WaitFor(control);

await builder.Build().RunAsync();

static string NewToken() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
