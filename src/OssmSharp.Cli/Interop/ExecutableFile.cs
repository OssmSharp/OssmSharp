namespace OssmSharp.Cli.Interop;

internal static class ExecutableFile
{
    internal static bool IsExecutable(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
            return false;

        return OperatingSystem.IsWindows()
            ? string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase)
            : (File.GetUnixFileMode(path)
                & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
    }
}
