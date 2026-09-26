using System.Diagnostics;
using System.Reflection;

namespace OSSMDotNet.DeviceCli.IntegrationTests;

/// <summary>
/// Checks the device CLI process exit and error output when simulator startup fails.
/// </summary>
public sealed class DeviceCliProcessTests
{
    [Fact]
    public async Task StartReportsTheHostStartupFailure()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));

        var result = await InvokeAsync(
            ["start", "--simulator-path", NonSimulatorPath()],
            timeout.Token);

        Assert.Equal(1, result.ExitCode);
        Assert.DoesNotContain("The simulator host is not running", result.Error, StringComparison.Ordinal);
        Assert.NotEmpty(result.Error);
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

    private static string NonSimulatorPath() => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "where.exe")
        : "/usr/bin/false";
}
