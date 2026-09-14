using System.Buffers.Text;
using System.Security.Cryptography;
using Aspire.Hosting.ApplicationModel;

var builder = DistributedApplication.CreateBuilder(args);
if (!builder.ExecutionContext.IsRunMode)
    throw new InvalidOperationException("This simulator prototype supports local Aspire runs only.");

var simulatorDirectory = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", "..", "tools", "ossm-sim-bridge"));
var simulatorPath = Path.Combine(simulatorDirectory, "target", "release",
    OperatingSystem.IsWindows() ? "ossm-sim-bridge.exe" : "ossm-sim-bridge");
var simulatorBuild = builder.AddExecutable("simulator-build", "cargo", simulatorDirectory, "build", "--release", "--locked");

var operatorValue = NewToken();
var operatorToken = builder.AddParameter("operator-token", operatorValue, secret: true);
var forwardingToken = builder.AddParameter("guest-forwarding-token", NewToken(), secret: true);
var pipeName = $"ossm-{Guid.NewGuid():N}";
var guest = builder.AddProject<Projects.GuestWeb>("guest")
    .WithEnvironment("DOTNET_ENVIRONMENT", "Development")
    .WithHttpEndpoint(env: "OSSM__GuestPort", isProxied: false)
    .WithEndpoint("http", endpoint => endpoint.TargetHost = "127.0.0.1")
    .WithHttpHealthCheck("/health")
    .WithEnvironment("OSSM__PipeName", pipeName)
    .WithEnvironment("OSSM__GuestServiceToken", forwardingToken)
    .WithUrls(context =>
    {
        var origin = $"http://127.0.0.1:{context.GetEndpoint("http")!.Port}";
        context.Urls.Clear();
        context.Urls.Add(new ResourceUrlAnnotation { Url = origin, DisplayText = "Guest (local only)" });
    });

var admin = builder.AddProject<Projects.AdminService>("admin")
    .WithEnvironment("DOTNET_ENVIRONMENT", "Development")
    .WithHttpEndpoint(env: "OSSM__AdminPort", isProxied: false)
    .WithEndpoint("http", endpoint => endpoint.TargetHost = "127.0.0.1")
    .WithHttpHealthCheck("/health")
    .WithEnvironment("OSSM__GuestOrigin", ReferenceExpression.Create($"http://127.0.0.1:{guest.GetEndpoint("http").Property(EndpointProperty.Port)}"))
    .WithEnvironment("OSSM__PipeName", pipeName)
    .WithEnvironment("OSSM__GuestServiceToken", forwardingToken)
    .WithEnvironment("OSSM__OperatorToken", operatorToken)
    .WithEnvironment("OSSM__SimulatorPath", simulatorPath)
    .WaitForCompletion(simulatorBuild)
    .WithUrls(context =>
    {
        var origin = $"http://127.0.0.1:{context.GetEndpoint("http")!.Port}";
        context.Urls.Clear();
        context.Urls.Add(new ResourceUrlAnnotation
        {
            Url = $"{origin}/#operator={operatorValue}",
            DisplayText = "Private operator (credential link)"
        });
    });

guest.WaitFor(admin);

await builder.Build().RunAsync();

static string NewToken() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
