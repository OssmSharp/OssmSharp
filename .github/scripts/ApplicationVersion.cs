using System.Reflection;

namespace OssmSharp;

/// <summary>
/// Writes the version stamped into the application that compiles this helper.
/// </summary>
internal static class ApplicationVersion
{
    /// <summary>
    /// Writes the application version when <c>--version</c> is the only argument.
    /// </summary>
    /// <param name="args">The application's command-line arguments.</param>
    /// <param name="output">Receives the informational version and a newline.</param>
    /// <returns>
    /// <see langword="true"/> when the version was written; otherwise
    /// <see langword="false"/>, leaving <paramref name="output"/> untouched.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// The application assembly has no informational version.
    /// </exception>
    internal static bool TryWrite(string[] args, TextWriter output)
    {
        if (args is not ["--version"])
        {
            return false;
        }

        var version = typeof(ApplicationVersion).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?? throw new InvalidOperationException("The application is missing its build version.");
        output.WriteLine(version.InformationalVersion);
        return true;
    }
}
