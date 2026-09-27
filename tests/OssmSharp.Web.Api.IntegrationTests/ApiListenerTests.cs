using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Hosting;

namespace OssmSharp.Web.Api.IntegrationTests;

/// <summary>
/// Verifies that listener overrides cannot expose additional API endpoints.
/// </summary>
public sealed class ApiListenerTests
{
    private const string AdminKey = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaA";
    private const string GuestKey = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbQ";

    [Theory]
    [InlineData("ASPNETCORE_URLS")]
    [InlineData("DOTNET_URLS")]
    public async Task HostingUrlOverrideCannotExposeAnAdditionalListener(string setting)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        await using var simulator = new SimulatorBridgeFixture();
        await simulator.InitializeAsync();
        var configuredPort = AvailablePort();
        var overridePort = AvailablePort();
        using var process = StartApi(configuredPort, simulator.Endpoint, overridePort: overridePort, overrideSetting: setting);
        try
        {
            await WaitForStartAsync(process, configuredPort, timeout.Token);
            using var probe = new TcpClient();
            await Assert.ThrowsAsync<SocketException>(async () =>
                await probe.ConnectAsync(IPAddress.Loopback, overridePort, timeout.Token));
            Assert.False(process.HasExited);
        }
        finally
        {
            await StopIfRunningAsync(process);
        }
    }

    private static Process StartApi(
        int port, string simulatorEndpoint, int? overridePort = null, string? overrideSetting = null)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo("dotnet")
            {
                CreateNoWindow = true,
                UseShellExecute = false
            },
        };
        process.StartInfo.ArgumentList.Add(ApiAssemblyPath());
        SetEnvironment(process.StartInfo.Environment, port, simulatorEndpoint);
        if (overridePort is { } portValue && overrideSetting is not null)
            process.StartInfo.Environment[overrideSetting] = $"http://0.0.0.0:{portValue}";
        Assert.True(process.Start());
        return process;
    }

    private static async Task WaitForStartAsync(Process process, int port, CancellationToken cancellationToken)
    {
        using var handler = new SocketsHttpHandler { UseProxy = false };
        using var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(1)
        };
        while (!process.HasExited)
        {
            if (await IsHealthyAsync(client, port, cancellationToken))
                return;
            await Task.Delay(50, cancellationToken);
        }
        throw new Xunit.Sdk.XunitException($"Api exited before startup. Exit code: {process.ExitCode}.");
    }

    private static async Task<bool> IsHealthyAsync(HttpClient client, int port, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await client.GetAsync($"http://127.0.0.1:{port}/health", cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private static async Task StopIfRunningAsync(Process process)
    {
        if (process.HasExited)
            return;
        process.Kill(entireProcessTree: true);
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await process.WaitForExitAsync(cleanup.Token);
    }

    private static void SetEnvironment(IDictionary<string, string?> environment, int port, string simulatorEndpoint)
    {
        environment["DOTNET_ENVIRONMENT"] = Environments.Development;
        environment["OSSM__Control__BindAddress"] = "127.0.0.1";
        environment["OSSM__Control__HostName"] = "127.0.0.1";
        environment["OSSM__Control__ControlPort"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        environment["OSSM__Control__GuestOrigin"] = $"http://127.0.0.1:{AvailablePort()}";
        environment["OSSM__Control__SimulatorEndpoint"] = simulatorEndpoint;
        environment["OSSM__Control__AdminWebServiceKey"] = AdminKey;
        environment["OSSM__Control__GuestWebServiceKey"] = GuestKey;
    }

    private static string ApiAssemblyPath()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "OssmSharp.Web.Api.dll");
        Assert.True(File.Exists(path), "The Web.Api project output must be available to this integration test.");
        return path;
    }

    private static int AvailablePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
