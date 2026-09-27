using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace OssmSharp.Cli.IntegrationTests;

/// <summary>
/// Exercises the actual CLI process and native simulator launch or attachment.
/// </summary>
[Collection("NativeSimulatorProcessTests")]
public sealed class DeviceCliProcessTests
{
    [Fact]
    public async Task PromptRejectsUnavailableSimulatorBeforeConnecting()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));

        var result = await InvokeAsync(
            ["prompt", "--simulator", Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))],
            timeout.Token);

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("existing executable", result.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("existing executable", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PromptLaunchControlsNativeSimulatorAndExitsOnEof()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var cli = StartCli(
            ["prompt", "--simulator", SimulatorPromptIntegrationTests.SimulatorPath()]);

        try
        {
            await cli.StandardInput.WriteLineAsync("status".AsMemory(), timeout.Token);
            var status = await ReadUntilAsync(
                cli.StandardOutput,
                cli.StandardError,
                "simulator control available",
                timeout.Token);

            await cli.StandardInput.WriteLineAsync(
                "set --speed 12 --stroke 10 --depth 20 --pattern SimpleStroke".AsMemory(),
                timeout.Token);
            await cli.StandardInput.WriteLineAsync("stop".AsMemory(), timeout.Token);
            cli.StandardInput.Close(); // The prompt must clean up the device and its child on EOF.

            var output = cli.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = cli.StandardError.ReadToEndAsync(timeout.Token);
            await cli.WaitForExitAsync(timeout.Token);

            Assert.Equal(0, cli.ExitCode);
            var results = status + Environment.NewLine + await output;
            var diagnostics = await error;
            Assert.Contains("Device:", status, StringComparison.Ordinal);
            Assert.Contains("Simulator write settled", results, StringComparison.Ordinal);
            Assert.Contains("Stop write settled", results, StringComparison.Ordinal);
            Assert.DoesNotContain("Device connection failed", diagnostics, StringComparison.Ordinal);
            Assert.DoesNotContain("Simulator write settled", diagnostics, StringComparison.Ordinal);
            Assert.DoesNotContain("Simulator connection opened.", results, StringComparison.Ordinal);
            Assert.DoesNotContain("Simulator connection closed.", results, StringComparison.Ordinal);
            Assert.Contains("Simulator connection opened.", diagnostics, StringComparison.Ordinal);
            Assert.Contains("Simulator connection closed.", diagnostics, StringComparison.Ordinal);
        }
        finally
        {
            await KillIfRunningAsync(cli);
        }
    }

    [Fact]
    public async Task PromptAttachSurvivesQuitAndEofWithoutTerminatingExternalSimulator()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var port = ReserveLoopbackPort();
        var endpoint = $"tcp://127.0.0.1:{port}";
        using var external = new Process
        {
            StartInfo = new ProcessStartInfo(SimulatorPromptIntegrationTests.SimulatorPath())
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
        external.StartInfo.ArgumentList.Add("--listen");
        external.StartInfo.ArgumentList.Add($"127.0.0.1:{port}");
        Assert.True(external.Start());

        try
        {
            Assert.Equal("ready", await external.StandardOutput.ReadLineAsync(timeout.Token));
            var quit = await InvokeAsync(
                ["prompt", "--simulator", endpoint],
                timeout.Token,
                "status\nquit\n");

            Assert.True(quit.ExitCode == 0, $"Expected a successful attach: {quit.Error}");
            Assert.Contains("simulator control available", quit.Output, StringComparison.Ordinal);
            Assert.DoesNotContain("simulator control available", quit.Error, StringComparison.Ordinal);
            Assert.DoesNotContain("Device connection failed", quit.Error, StringComparison.Ordinal);
            Assert.DoesNotContain("Simulator connection opened.", quit.Output, StringComparison.Ordinal);
            Assert.DoesNotContain("Simulator connection closed.", quit.Output, StringComparison.Ordinal);
            Assert.Contains("Simulator connection opened.", quit.Error, StringComparison.Ordinal);
            Assert.Contains("Simulator connection closed.", quit.Error, StringComparison.Ordinal);
            Assert.False(external.HasExited, "Attaching must not terminate an external simulator on quit.");

            var eof = await InvokeAsync(["prompt", "--simulator", endpoint], timeout.Token);
            Assert.True(eof.ExitCode == 0, $"Expected a successful EOF cleanup: {eof.Error}");
            Assert.False(external.HasExited, "An attached simulator must remain reusable after EOF.");
        }
        finally
        {
            await KillIfRunningAsync(external);
        }
    }

    [Fact]
    public async Task PromptAttachUnavailableReportsFailure()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        var port = ReserveLoopbackPort();

        var failed = await InvokeAsync(["prompt", "--simulator", $"tcp://127.0.0.1:{port}"], timeout.Token);

        Assert.Equal(1, failed.ExitCode);
        Assert.Contains("Device connection failed", failed.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("Device connection failed", failed.Output, StringComparison.Ordinal);
    }

    private static async Task<(int ExitCode, string Output, string Error)> InvokeAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        string? input = null)
    {
        using var process = StartCli(arguments);
        try
        {
            if (input is not null)
                await process.StandardInput.WriteAsync(input.AsMemory(), cancellationToken);
            process.StandardInput.Close();
            var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var error = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            return (process.ExitCode, await output, await error);
        }
        finally
        {
            await KillIfRunningAsync(process);
        }
    }

    private static Process StartCli(IReadOnlyList<string> arguments)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
        process.StartInfo.ArgumentList.Add(DeviceCliAssemblyPath());
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        Assert.True(process.Start());
        return process;
    }

    private static async Task<string> ReadUntilAsync(
        StreamReader reader,
        StreamReader diagnostics,
        string expected,
        CancellationToken cancellationToken)
    {
        var lines = new List<string>();
        for (var index = 0; index < 20; index++)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
                break;
            lines.Add(line);
            if (line.Contains(expected, StringComparison.Ordinal))
                return string.Join(Environment.NewLine, lines);
        }

        var error = await diagnostics.ReadToEndAsync(cancellationToken);
        throw new InvalidDataException(
            $"The CLI did not report {expected} before exiting. Stdout: {string.Join(Environment.NewLine, lines)}; stderr: {error}");
    }

    private static int ReserveLoopbackPort()
    {
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        return ((IPEndPoint)reservation.LocalEndpoint).Port;
    }

    private static async Task KillIfRunningAsync(Process process)
    {
        if (process.HasExited)
            return;

        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) when (process.HasExited)
        {
            // The CLI exited between the check and the emergency cleanup.
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await process.WaitForExitAsync(timeout.Token);
    }

    private static string DeviceCliAssemblyPath()
    {
        var path = Path.Combine(
            Path.GetDirectoryName(typeof(DeviceCliProcessTests).Assembly.Location)!,
            "OssmSharp.Cli.dll");
        Assert.True(File.Exists(path), "The OssmSharp.Cli project output must be available to this integration test.");
        return path;
    }

}
