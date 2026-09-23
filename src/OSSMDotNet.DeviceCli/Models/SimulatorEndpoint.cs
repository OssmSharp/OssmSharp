using System.Security.Cryptography;
using System.Text;

namespace OSSMDotNet.DeviceCli.Models;

/// <summary>
/// Identifies the current user's private pipe and exclusive ownership lock for one simulator host.
/// </summary>
/// <param name="PipeName">The private pipe name for the host.</param>
/// <param name="LockFilePath">The path of the host's exclusive ownership lock.</param>
internal sealed record SimulatorEndpoint(string PipeName, string LockFilePath)
{
    private const string Prefix = "OSSMDotNet.DeviceCli";

    /// <summary>
    /// Creates the stable endpoint for the current user.
    /// </summary>
    /// <returns>The current user's simulator host endpoint.</returns>
    internal static SimulatorEndpoint CreateDefault()
    {
        var identity = $"{Environment.UserDomainName}\\{Environment.UserName}";
        var suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..16];
        return Create(suffix);
    }

    /// <summary>
    /// Creates an isolated endpoint for one test or temporary host.
    /// </summary>
    /// <returns>A unique simulator host endpoint.</returns>
    internal static SimulatorEndpoint CreateUnique() => Create(Guid.NewGuid().ToString("N"));

    private static SimulatorEndpoint Create(string suffix) =>
        new(
            $"{Prefix}.{suffix}",
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                $"{Prefix}.{suffix}.lock"));
}
