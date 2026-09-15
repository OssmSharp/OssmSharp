using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

namespace OSSMDotNet.Web.AppHost.Tests;

internal static class BrowserJourney
{
    public static async Task RunAsync(Uri admin, Uri guest, string browser, CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(typeof(BrowserJourney).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "RepositoryRoot").Value!);
        var start = new ProcessStartInfo("python")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = root
        };
        start.ArgumentList.Add(Path.Combine(root, "tests", "browser", "prototype_smoke.py"));
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Unable to start browser verification.");
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errors = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new
            {
                adminUrl = admin.GetLeftPart(UriPartial.Authority),
                guestOrigin = guest.GetLeftPart(UriPartial.Authority),
                browser
            }).AsMemory(), cancellationToken);
            process.StandardInput.Close();
            await process.WaitForExitAsync(cancellationToken);
            Assert.True(process.ExitCode == 0, $"{browser} browser verification failed: {await output} {await errors}");
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
    }
}
