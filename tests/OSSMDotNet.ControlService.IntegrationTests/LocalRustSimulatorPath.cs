using System.Reflection;

namespace OSSMDotNet.ControlService.IntegrationTests;

internal static class LocalRustSimulatorPath
{
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
