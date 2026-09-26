using OssmSharp.Device.Runtime;

namespace OssmSharp.Cli.IntegrationTests;

internal static class IsolatedSimulatorHost
{
    internal static async Task<int> RunAsync(
        string simulatorPath,
        SimulatorEndpoint endpoint,
        CancellationToken cancellationToken)
    {
        var root = OperatingSystem.IsWindows() ? "D:/temp" : Path.GetTempPath();
        var directory = Path.Combine(root, $"OssmSharp.Cli.IntegrationTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try
        {
            return await SimulatorHost.RunAsync(
                simulatorPath,
                endpoint,
                cancellationToken,
                () => DeviceOwnershipLock.TryAcquireAtPath(Path.Combine(directory, "device.lock")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
