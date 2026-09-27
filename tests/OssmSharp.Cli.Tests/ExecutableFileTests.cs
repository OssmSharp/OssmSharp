using OssmSharp.Cli.Interop;

namespace OssmSharp.Cli.Tests;

public sealed class ExecutableFileTests
{
    [Fact]
    public void ExistingPlatformExecutableIsAccepted()
    {
        var path = CreateExecutableFile();
        try
        {
            Assert.True(ExecutableFile.IsExecutable(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void MissingPathIsNotExecutable()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        Assert.False(ExecutableFile.IsExecutable(path));
    }

    [Fact]
    public void ExistingNonExecutableFileIsRejectedOnUnix()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var path = Path.GetTempFileName();
        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);

            Assert.False(ExecutableFile.IsExecutable(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string CreateExecutableFile()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"{Guid.NewGuid():N} executable{(OperatingSystem.IsWindows() ? ".exe" : string.Empty)}");
        File.WriteAllText(path, string.Empty);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return path;
    }
}
