using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace OSSMDotNet.GuestWeb.Tests;

public sealed class GuestApplicationTests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(0)]
    [InlineData(1023)]
    [InlineData(65536)]
    public void BootstrapPortRejectsOutsideUnprivilegedPortRange(int port)
    {
        Assert.Throws<OptionsValidationException>(() => Build(port));
    }

    [Fact]
    public async Task HealthAndStaticPagesAreGuestOnlyWithLocalAssetsAndStrictCsp()
    {
        var port = AvailablePort();
        await using var app = Build(port);
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
        await using var app = Build(port);
        await app.StartAsync(Cancellation);
        using var client = LocalClient(port);
        using var wrongHost = new HttpRequestMessage(HttpMethod.Get, "health");
        wrongHost.Headers.Host = $"localhost:{port}";
        using var hostResponse = await client.SendAsync(wrongHost, Cancellation);
        Assert.Equal(HttpStatusCode.BadRequest, hostResponse.StatusCode);

        using var content = new StringContent("");
        using var missingOrigin = await client.PostAsync("health", content, Cancellation);
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
    public async Task OpeningInvitationPagePrerendersWithoutContactingTheControlService()
    {
        var port = AvailablePort();
        await using var app = Build(port);
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
        await app.StopAsync(Cancellation);
    }

    private static WebApplication Build(int port)
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
            ["OSSM:Guest:GuestPort"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["OSSM:ControlClient:ControlOrigin"] = "http://127.0.0.1:5181",
            ["OSSM:ControlClient:ServiceKey"] = new string('a', 42) + "A"
        });
        return GuestApplication.Build(builder);
    }

    private static int AvailablePort()
    {
        using var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        return ((IPEndPoint)socket.LocalEndpoint).Port;
    }

    private static LocalHttpClient LocalClient(int port) => new(port);

    private sealed class LocalHttpClient : IDisposable
    {
        private readonly HttpClientHandler _handler = new() { UseProxy = false };
        private readonly HttpClient _client;

        internal LocalHttpClient(int port)
        {
            _client = new HttpClient(_handler, disposeHandler: false)
            {
                BaseAddress = new Uri($"http://127.0.0.1:{port}/"),
                Timeout = TimeSpan.FromSeconds(5)
            };
        }

        internal Task<HttpResponseMessage> GetAsync(string? requestUri, CancellationToken cancellationToken) =>
            _client.GetAsync(requestUri, cancellationToken);

        internal Task<HttpResponseMessage> PostAsync(
            string? requestUri,
            HttpContent? content,
            CancellationToken cancellationToken) =>
            _client.PostAsync(requestUri, content, cancellationToken);

        internal Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            _client.SendAsync(request, cancellationToken);

        public void Dispose()
        {
            _client.Dispose();
            _handler.Dispose();
        }
    }

    [Theory]
    [InlineData(1024)]
    [InlineData(65535)]
    public async Task ExactGuestPortBoundsServeHealthThroughTheLocalSecurityBoundary(int port)
    {
        await using var app = Build(port);
        await app.StartAsync(Cancellation);
        using var client = LocalClient(port);

        using var response = await client.GetAsync("health", Cancellation);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync(Cancellation));
        Assert.Equal("no-referrer", Assert.Single(response.Headers.GetValues("Referrer-Policy")));
        Assert.Equal("DENY", Assert.Single(response.Headers.GetValues("X-Frame-Options")));
        var csp = Assert.Single(response.Headers.GetValues("Content-Security-Policy"));
        Assert.Contains($"connect-src 'self' ws://127.0.0.1:{port}", csp, StringComparison.Ordinal);
        await app.StopAsync(Cancellation);
    }

    [Fact]
    public async Task AllowedLocalOriginCanLoadGuestAssetsWithTheCurrentBoundaryHeaders()
    {
        var port = AvailablePort();
        await using var app = Build(port);
        await app.StartAsync(Cancellation);
        using var client = LocalClient(port);
        using var request = new HttpRequestMessage(HttpMethod.Get, "guest.css");
        request.Headers.Add("Origin", $"http://127.0.0.1:{port}");

        using var response = await client.SendAsync(request, Cancellation);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("no-store", Assert.Single(response.Headers.GetValues("Cache-Control")), StringComparison.Ordinal);
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        var csp = Assert.Single(response.Headers.GetValues("Content-Security-Policy"));
        Assert.Contains("default-src 'self'", csp, StringComparison.Ordinal);
        Assert.DoesNotContain("unsafe-inline", csp, StringComparison.Ordinal);
        await app.StopAsync(Cancellation);
    }
}
