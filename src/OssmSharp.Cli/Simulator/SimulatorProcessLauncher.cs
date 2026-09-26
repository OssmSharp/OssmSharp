using System.Diagnostics;
using System.Reflection;

namespace OssmSharp.Cli.Simulator;

/// <summary>
/// Starts the hidden simulator host as a child of the diagnostic CLI process.
/// </summary>
internal static class SimulatorProcessLauncher
{
    /// <summary>
    /// Starts a hidden host and retains its early standard error for startup diagnostics.
    /// </summary>
    /// <param name="simulatorPath">The native simulator executable path.</param>
    /// <param name="endpoint">The private endpoint passed to the child host.</param>
    /// <returns>A tracker for the child host startup.</returns>
    internal static SimulatorHostLaunch Start(string simulatorPath, SimulatorEndpoint endpoint)
    {
        var processPath = Environment.ProcessPath
            ?? throw new InvalidOperationException("The current executable path is unavailable.");
        var entryAssemblyPath = Assembly.GetEntryAssembly()?.Location
            ?? throw new InvalidOperationException("The OssmSharp.Cli assembly path is unavailable.");
        var entryAssemblyName = Path.GetFileNameWithoutExtension(entryAssemblyPath);
        var processName = Path.GetFileNameWithoutExtension(processPath);
        var start = new ProcessStartInfo
        {
            FileName = processPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };

        if (!string.Equals(processName, entryAssemblyName, StringComparison.OrdinalIgnoreCase))
        {
            start.ArgumentList.Add(entryAssemblyPath);
        }

        start.ArgumentList.Add("__host");
        start.ArgumentList.Add("--simulator-path");
        start.ArgumentList.Add(simulatorPath);
        start.ArgumentList.Add("--pipe-name");
        start.ArgumentList.Add(endpoint.PipeName);
        start.ArgumentList.Add("--lock-file");
        start.ArgumentList.Add(endpoint.LockFilePath);

        var process = Process.Start(start)
            ?? throw new InvalidOperationException("The simulator host process could not be started.");
        return SimulatorHostLaunch.Create(process);
    }
}
