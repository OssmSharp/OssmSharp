using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace OssmSharp.AppHost;

internal static class SimulatorStartup
{
    internal const string HealthCheckName = "simulator-listener";

    internal static int AvailablePort()
    {
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        return ((IPEndPoint)reservation.LocalEndpoint).Port;
    }

    internal static void RegisterHealthCheck(IServiceCollection services, int port)
    {
        services.AddHealthChecks().AddAsyncCheck(HealthCheckName, async cancellationToken =>
        {
            try
            {
                using var probe = new TcpClient(AddressFamily.InterNetwork);
                await probe.ConnectAsync(IPAddress.Loopback, port, cancellationToken);
                return HealthCheckResult.Healthy();
            }
            catch (SocketException)
            {
                return HealthCheckResult.Unhealthy();
            }
        });
    }
}
