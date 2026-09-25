using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OSSMDotNet;
using OSSMDotNet.Simulator;
using OSSMDotNet.Simulator.Models;

namespace OSSMDotNet.DeviceCli.Simulator;

/// <summary>
/// Owns the exclusive process lifetime of a local simulator host.
/// </summary>
internal static class SimulatorHost
{
    /// <summary>
    /// Runs a host while holding its exclusive ownership lock.
    /// </summary>
    /// <param name="simulatorPath">The native simulator executable to own.</param>
    /// <param name="endpoint">The host endpoint and ownership lock.</param>
    /// <returns>The host exit code, or one if the ownership lock is unavailable.</returns>
    internal static async Task<int> RunAsync(
        string simulatorPath,
        SimulatorEndpoint endpoint,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(simulatorPath);
        ArgumentNullException.ThrowIfNull(endpoint);

        await using var ownership = TryAcquire(endpoint.LockFilePath);
        if (ownership is null)
        {
            return 1;
        }

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

    private static FileStream? TryAcquire(string lockFilePath)
    {
        try
        {
            return new FileStream(
                lockFilePath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.Asynchronous | FileOptions.DeleteOnClose);
        }
        catch (IOException)
        {
            return null;
        }
    }
}
