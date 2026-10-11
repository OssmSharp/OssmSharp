#nullable enable
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

/// <summary>
/// Checks an extracted web application using real listeners, without a device.
/// </summary>
public static class ReleaseSmoke
{
    /// <summary>
    /// Checks production assets, development health, and default and configured log directories.
    /// </summary>
    /// <param name="executable">The extracted native executable.</param>
    /// <param name="workingDirectory">An isolated writable directory.</param>
    /// <returns>A task that completes when the checks and process cleanup finish.</returns>
    public static async Task CheckWebAsync(string executable, string workingDirectory)
    {
        await CheckEnvironmentAsync(executable, workingDirectory, "Production");
        await CheckEnvironmentAsync(executable, workingDirectory, "Development");
    }

    private static async Task CheckEnvironmentAsync(string executable, string workingDirectory, string environment)
    {
        var listeners = Enumerable.Range(0, 3).Select(_ => new TcpListener(IPAddress.Loopback, 0)).ToArray();
        int[] ports;
        try
        {
            foreach (var listener in listeners)
                listener.Start();
            ports = listeners.Select(listener => ((IPEndPoint)listener.LocalEndpoint).Port).ToArray();
        }
        finally
        {
            foreach (var listener in listeners)
                listener.Stop();
        }

        var origins = ports.Select(port => $"http://127.0.0.1:{port}").ToArray();
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var key in start.Environment.Keys.Where(key =>
            key.StartsWith("OSSM__", StringComparison.OrdinalIgnoreCase) ||
            key.StartsWith("ASPNETCORE_", StringComparison.OrdinalIgnoreCase) ||
            key.StartsWith("Logging__", StringComparison.OrdinalIgnoreCase) ||
            key.StartsWith("OTEL_", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(key);
        start.Environment["OSSM__Web__ApiPort"] = ports[0].ToString();
        start.Environment["OSSM__Web__GuestPort"] = ports[1].ToString();
        start.Environment["OSSM__Web__AdminPort"] = ports[2].ToString();
        start.Environment["OSSM__Control__DeviceIdentifier"] = "";
        start.Environment["OSSM__Guest__Hostname"] = "";
        var logDirectory = Path.Combine(workingDirectory, environment == "Production" ? "logs" : "configured-logs");
        if (environment == "Development")
            start.Environment["OSSM__FileLogging__Directory"] = logDirectory;
        start.Environment["DOTNET_ENVIRONMENT"] = environment;
        start.Environment["ASPNETCORE_ENVIRONMENT"] = environment;
        start.Environment["HOME"] = workingDirectory;
        start.Environment["USERPROFILE"] = workingDirectory;
        start.Environment["LOCALAPPDATA"] = workingDirectory;
        start.Environment["Logging__Console__LogLevel__Default"] = "Information";
        start.Environment["DOTNET_ROOT"] = Path.Combine(workingDirectory, "no-shared-runtime");
        start.Environment["DOTNET_ROOT_ARM64"] = start.Environment["DOTNET_ROOT"];
        start.Environment["DOTNET_ROOT_X64"] = start.Environment["DOTNET_ROOT"];
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var listening = new HashSet<string>();
        var diagnostics = new Queue<string>();
        using var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        void Record(object sender, DataReceivedEventArgs args)
        {
            if (args.Data is not { } line)
                return;
            lock (diagnostics)
            {
                if (diagnostics.Count == 30)
                    diagnostics.Dequeue();
                diagnostics.Enqueue(line);
                foreach (var origin in origins)
                    if (line.Contains("Now listening on: " + origin, StringComparison.Ordinal))
                        listening.Add(origin);
                if (listening.Count == origins.Length)
                    ready.TrySetResult();
            }
        }

        process.OutputDataReceived += Record;
        process.ErrorDataReceived += Record;
        process.Exited += (_, _) => ready.TrySetException(
            new InvalidOperationException($"Web application exited during startup ({process.ExitCode})."));
        if (!process.Start())
            throw new InvalidOperationException("The web application did not start.");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            // Listener log events establish readiness without polling or sleeps.
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(60));
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            foreach (var origin in origins)
            {
                using var health = await http.GetAsync(origin + "/health");
                if (environment == "Development")
                    health.EnsureSuccessStatusCode();
                else if (health.StatusCode != HttpStatusCode.NotFound)
                    throw new InvalidOperationException("Production must not expose development health endpoints.");
            }
            foreach (var path in new[]
            {
                origins[2] + "/",
                origins[2] + "/admin.css",
                origins[1] + "/guest.css",
                origins[2] + "/_content/OssmSharp.Web.Common/controls.css",
                origins[1] + "/_content/OssmSharp.Web.Common/controls.css",
                origins[2] + "/_framework/blazor.web.js",
                origins[1] + "/_framework/blazor.web.js",
            })
            {
                using var response = await http.GetAsync(path);
                response.EnsureSuccessStatusCode();
                if ((await response.Content.ReadAsByteArrayAsync()).Length == 0)
                    throw new InvalidOperationException("Packaged content was empty: " + path);
            }
            if (process.HasExited)
                throw new InvalidOperationException("The web application exited during its content checks.");
            if (!Directory.Exists(logDirectory))
                throw new InvalidOperationException("The application did not use the expected external log directory.");
        }
        catch (Exception exception)
        {
            lock (diagnostics)
                throw new InvalidOperationException(
                    $"Extracted {environment} web application check failed: {exception.Message}\n" +
                    string.Join("\n", diagnostics), exception);
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }
    }
}
