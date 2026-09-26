using System.Diagnostics;
using System.Reflection;

namespace OssmSharp.Cli.IntegrationTests;

/// <summary>
/// Checks device CLI process errors before simulator startup.
/// </summary>
public sealed class DeviceCliProcessTests
{
    [Fact]
    public async Task PromptRejectsUnavailableSimulatorBeforeConnecting()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));

        var result = await InvokeAsync(
            ["prompt", "--simulator", Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))],
            timeout.Token);

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("absolute path to an existing simulator", result.Error, StringComparison.Ordinal);
    }

    private static async Task<(int ExitCode, string Output, string Error)> InvokeAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                UseShellExecute = false,
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
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return (process.ExitCode, await output, await error);
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
