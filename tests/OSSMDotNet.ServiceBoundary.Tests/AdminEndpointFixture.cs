using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using OSSMDotNet.Models;
using OSSMDotNet.AdminService;
using OSSMDotNet.ServiceDefaults;

namespace OSSMDotNet.ServiceBoundary.Tests;

internal sealed class AdminEndpointFixture : IAsyncDisposable
{
    private readonly WebApplication _app;
    public HttpClient Client { get; }
    public FakeTimeProvider Time { get; } = new();
    public string ForwardingToken { get; } = CapabilityTokens.NewToken();
    public IServiceProvider Services => _app.Services;
    public ICollection<string> Addresses => _app.Urls;

    public AdminEndpointFixture(IReadOnlyList<string>? styleHashes = null,
        IReadOnlyDictionary<string, string?>? configuration = null, IDeviceBridge? bridge = null)
    {
        using var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        var port = ((IPEndPoint)socket.LocalEndpoint).Port;
        socket.Stop();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = [],
            EnvironmentName = "Development",
            ApplicationName = typeof(AdminEndpoints).Assembly.GetName().Name
        });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OSSM:AdminPort"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["OSSM:GuestOrigin"] = $"http://127.0.0.1:{(port == 5182 ? 5183 : 5182)}",
            ["OSSM:PipeName"] = $"test-{Guid.NewGuid():N}",
            ["OSSM:GuestServiceToken"] = ForwardingToken,
            ["OSSM:SimulatorPath"] = "unused-in-endpoint-tests"
        });
        if (configuration is not null) builder.Configuration.AddInMemoryCollection(configuration);
        builder.Services.AddSingleton<TimeProvider>(Time);
        builder.Services.AddAdminConfiguration().AddLocalBlazor().AddAdminControl().AddAdminListener();
        if (bridge is not null) builder.Services.AddSingleton(bridge);
        _app = builder.Build();
        _app.UseWebSockets();
        _app.UseLocalBoundary(port, styleHashes);
        _app.MapAdminEndpoints();
        Client = new HttpClient(new HttpClientHandler { UseProxy = false })
        {
            BaseAddress = new Uri($"http://127.0.0.1:{port}"),
            Timeout = TimeSpan.FromSeconds(5)
        };
        Client.DefaultRequestHeaders.Add("Origin", Client.BaseAddress.GetLeftPart(UriPartial.Authority));
    }

    public Task StartAsync() => _app.StartAsync(TestContext.Current.CancellationToken);

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.StopAsync(TestContext.Current.CancellationToken);
        await _app.DisposeAsync();
    }
}
