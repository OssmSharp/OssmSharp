using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;

namespace OssmSharp.Device.Simulator.IntegrationTests;

public sealed class SimulatorBridgeFixture : IAsyncLifetime
{
    private Process? _process;

    public string Endpoint { get; private set; } = string.Empty;

    public bool IsRunning => _process is { HasExited: false };

    public async ValueTask InitializeAsync()
    {
        var root = Path.GetFullPath(typeof(SimulatorBridgeFixture).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "RepositoryRoot").Value!);
        var executable = OperatingSystem.IsWindows() ? "ossm-sim-bridge.exe" : "ossm-sim-bridge";
        var path = Path.Combine(root, "tools", "ossm-sim-bridge", "target", "release", executable);
        Assert.True(File.Exists(path), "Build the pinned OSSM-RS simulator adapter before running integration tests.");

        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        Endpoint = $"tcp://127.0.0.1:{port}";

        var startInfo = new ProcessStartInfo(path)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("--listen");
        startInfo.ArgumentList.Add($"127.0.0.1:{port}");
        _process = Process.Start(startInfo) ?? throw new IOException("The test simulator did not start.");

        try
        {
            using var readiness = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (true)
            {
                try
                {
                    await ReadSpeedAsync(readiness.Token);
                    break;
                }
                catch (Exception exception) when (
                    exception is SocketException or IOException && !readiness.IsCancellationRequested && IsRunning)
                {
                    await Task.Delay(25, readiness.Token);
                }
            }

            await Task.Delay(50, readiness.Token);
        }
        catch
        {
            await DisposeAsync();
            throw;
        }
    }

    public async Task<int> ReadSpeedAsync(CancellationToken cancellationToken)
    {
        var endpoint = new Uri(Endpoint);
        using var client = new TcpClient(AddressFamily.InterNetwork);
        await client.ConnectAsync(IPAddress.Loopback, endpoint.Port, cancellationToken);
        using var stream = client.GetStream();
        await stream.WriteAsync("{\"v\":1,\"id\":1,\"op\":\"status\"}\n"u8.ToArray(), cancellationToken);
        using var reader = new StreamReader(stream);

        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken)
                ?? throw new IOException("The simulator closed the status connection.");
            using var reply = JsonDocument.Parse(line);
            if (reply.RootElement.TryGetProperty("id", out var id) && id.GetInt32() == 1)
            {
                Assert.True(reply.RootElement.GetProperty("ok").GetBoolean());
                return reply.RootElement.GetProperty("state").GetProperty("desired").GetProperty("speed").GetInt32();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_process is not { } process)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill();
            }

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(timeout.Token);
        }
        finally
        {
            process.Dispose();
            _process = null;
        }
    }
}
