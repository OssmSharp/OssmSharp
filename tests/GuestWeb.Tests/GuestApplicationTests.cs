using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace OSSMDotNet.Web.GuestWeb.Tests;

public sealed class GuestApplicationTests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(0)]
    [InlineData(1023)]
    [InlineData(65536)]
    public void BootstrapPortRejectsOutsideUnprivilegedPortRange(int port)
    {
        Assert.Throws<OptionsValidationException>(() => Build(port, "test-pipe"));
    }

    [Fact]
    public async Task HealthAndStaticPagesAreGuestOnlyWithLocalAssetsAndStrictCsp()
    {
        var port = AvailablePort();
        await using var app = Build(port, $"test-{Guid.NewGuid():N}");
        await app.StartAsync(Cancellation);
        using var client = LocalClient(port);

        using var healthResponse = await client.GetAsync("health", Cancellation);
        Assert.Equal(HttpStatusCode.OK, healthResponse.StatusCode);
        Assert.Equal("Healthy", await healthResponse.Content.ReadAsStringAsync(Cancellation));

        using var home = await client.GetAsync("", Cancellation);
        Assert.Equal(HttpStatusCode.OK, home.StatusCode);
        var html = await home.Content.ReadAsStringAsync(Cancellation);
        Assert.Contains("Open a valid invitation from the operator.", html);
        Assert.DoesNotContain("INVITE REQUIRED.", html);
        Assert.Contains("rw-notice rw-operation-notice", html);
        Assert.Contains("<rw-alert", html);
        Assert.Contains("rw-theme", html);
        Assert.Contains("_content/RetroWebUi.Blazor/widgets/theme.", html);
        Assert.DoesNotContain("data-input", html);
        Assert.Contains("_framework/blazor.web.js", html);
        Assert.Contains("id=\"guest-start\"", html);
        Assert.Matches("src=\"guest-start\\.[^\"]+\\.js\"", html);
        Assert.Matches("data-controller=\"guest-session\\.[^\"]+\\.js\"", html);
        var csp = string.Join("; ", home.Headers.GetValues("Content-Security-Policy"));
        Assert.Contains("script-src 'self'", csp);
        Assert.DoesNotContain("unsafe-inline", csp);
        Assert.Equal("no-referrer", Assert.Single(home.Headers.GetValues("Referrer-Policy")));

        foreach (var asset in new[] { "guest.css", "guest-start.js", "guest-session.js", "_content/SharedUi/controls.css",
            "_content/SharedUi/connection-ui.js",
            "_content/RetroWebUi.Blazor/widgets/theme.css", "_content/RetroWebUi.Blazor/widgets/index.js", "_framework/blazor.web.js" })
        {
            using var response = await client.GetAsync(asset, Cancellation);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        foreach (var forbiddenRoute in new[] { "admin", "api/admin/status", "arm", "unlock" })
        {
            using var response = await client.GetAsync(forbiddenRoute, Cancellation);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
        await app.StopAsync(Cancellation);
    }

    [Fact]
    public async Task HttpBoundaryRejectsForeignHostAndMissingOrForeignPostOrigin()
    {
        var port = AvailablePort();
        await using var app = Build(port, $"test-{Guid.NewGuid():N}");
        await app.StartAsync(Cancellation);
        using var client = LocalClient(port);
        using var wrongHost = new HttpRequestMessage(HttpMethod.Get, "health");
        wrongHost.Headers.Host = $"localhost:{port}";
        using var hostResponse = await client.SendAsync(wrongHost, Cancellation);
        Assert.Equal(HttpStatusCode.BadRequest, hostResponse.StatusCode);

        using var missingOrigin = await client.PostAsync("health", new StringContent(""), Cancellation);
        Assert.Equal(HttpStatusCode.Forbidden, missingOrigin.StatusCode);
        using var wrongOrigin = new HttpRequestMessage(HttpMethod.Post, "health");
        wrongOrigin.Headers.Add("Origin", "https://example.invalid");
        using var originResponse = await client.SendAsync(wrongOrigin, Cancellation);
        Assert.Equal(HttpStatusCode.Forbidden, originResponse.StatusCode);

        using var websocket = new HttpRequestMessage(HttpMethod.Get, "_blazor");
        websocket.Headers.Add("Connection", "Upgrade");
        websocket.Headers.Add("Upgrade", "websocket");
        websocket.Headers.Add("Sec-WebSocket-Version", "13");
        websocket.Headers.Add("Sec-WebSocket-Key", Convert.ToBase64String(Guid.NewGuid().ToByteArray()));
        using var websocketResponse = await client.SendAsync(websocket, Cancellation);
        Assert.Equal(HttpStatusCode.Forbidden, websocketResponse.StatusCode);
        await app.StopAsync(Cancellation);
    }

    [Fact]
    public async Task OpeningInvitationPagePrerendersWithoutContactingPipe()
    {
        var port = AvailablePort();
        var pipeName = $"test-{Guid.NewGuid():N}";
        await using var pipe = new System.IO.Pipes.NamedPipeServerStream(pipeName,
            System.IO.Pipes.PipeDirection.InOut, 1, System.IO.Pipes.PipeTransmissionMode.Byte,
            System.IO.Pipes.PipeOptions.Asynchronous);
        await using var app = Build(port, pipeName);
        await app.StartAsync(Cancellation);
        using var client = LocalClient(port);

        using var page = await client.GetAsync("join/test-session", Cancellation);

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync(Cancellation);
        Assert.Contains("Opening invite...", html);
        Assert.Contains("rw-notice rw-operation-notice", html);
        Assert.Contains("components-reconnect-modal", html);
        Assert.DoesNotContain("Connection lost. Open the invitation link again.", html);
        Assert.DoesNotContain("Join simulator session", html);
        Assert.DoesNotContain("data-input", html);
        Assert.False(pipe.IsConnected);
        await app.StopAsync(Cancellation);
    }

    private static WebApplication Build(int port, string pipeName)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = [],
            ApplicationName = typeof(GuestApplication).Assembly.GetName().Name,
            ContentRootPath = AppContext.BaseDirectory,
            EnvironmentName = Environments.Development
        });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OSSM:GuestPort"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["OSSM:PipeName"] = pipeName,
            ["OSSM:GuestServiceToken"] = new string('a', 43)
        });
        return GuestApplication.Build(builder);
    }

    private static int AvailablePort()
    {
        using var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        return ((IPEndPoint)socket.LocalEndpoint).Port;
    }

    private static HttpClient LocalClient(int port) =>
        new(new HttpClientHandler { UseProxy = false })
        {
            BaseAddress = new Uri($"http://127.0.0.1:{port}/"),
            Timeout = TimeSpan.FromSeconds(5)
        };
}
