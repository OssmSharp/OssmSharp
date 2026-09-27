using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OssmSharp.Device.Runtime;
using OssmSharp.Device.Simulator;
using OssmSharp.Device.Simulator.Models;

namespace OSSMDotNet.DeviceCli.Simulator;

/// <summary>
/// Owns the exclusive process lifetime of a local simulator host.
/// </summary>
internal static class SimulatorHost
{
    /// <summary>
    /// Runs a local simulator host for the selected process.
    /// </summary>
    /// <param name="simulatorPath">The native simulator executable to own.</param>
    /// <param name="endpoint">The host endpoint.</param>
    /// <returns>The host exit code.</returns>
    internal static async Task<int> RunAsync(
        string simulatorPath,
        SimulatorEndpoint endpoint,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(simulatorPath);
        ArgumentNullException.ThrowIfNull(endpoint);

        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddScoped<DeviceBase>(services => new SimulatedDevice(
            Options.Create(new SimulatedDeviceOptions { ExecutablePath = simulatorPath }),
            TimeProvider.System,
            services.GetRequiredService<ILogger<SimulatedDevice>>()));
        builder.Services.AddScoped(services => new SimulatorHostSession(
            simulatorPath, endpoint, services.GetRequiredService<DeviceBase>()));

        using var host = builder.Build();
        await using var scope = host.Services.CreateAsyncScope();
        var session = scope.ServiceProvider.GetRequiredService<SimulatorHostSession>();
        return await session.RunAsync(cancellationToken).ConfigureAwait(false);
    }
}
