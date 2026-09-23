using System.Reflection;

namespace OSSMDotNet.Api.IntegrationTests;

/// <summary>
/// Locates the prebuilt Rust simulator adapter used by integration tests.
/// </summary>
internal static class LocalRustSimulatorPath
{
    /// <summary>
    /// Resolves the release adapter under the repository root recorded in assembly metadata.
    /// </summary>
    /// <returns>The path to the existing adapter executable.</returns>
    /// <exception cref="FileNotFoundException">The release adapter has not been built.</exception>
    internal static string Resolve()
    {
        var root = Path.GetFullPath(typeof(LocalRustSimulatorPath).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "RepositoryRoot").Value!);
        var executable = OperatingSystem.IsWindows() ? "ossm-sim-bridge.exe" : "ossm-sim-bridge";
        var path = Path.Combine(root, "tools", "ossm-sim-bridge", "target", "release", executable);
        if (!File.Exists(path))
            throw new FileNotFoundException(
                "Build the Rust simulator adapter with cargo build --release --locked before these integration tests.",
                path);
        return path;
    }
}
