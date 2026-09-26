using System.Diagnostics;

namespace OssmSharp.Cli.Simulator;

/// <summary>
/// Retains a newly launched host long enough to surface a bounded startup failure.
/// </summary>
internal sealed class SimulatorHostLaunch : IDisposable
{
    private const int MaximumErrorCharacters = 4_096;
    private readonly Task<string>? _standardError;
    private readonly Task<int>? _host;
    private readonly Process? _process;

    private SimulatorHostLaunch(Process process)
    {
        _process = process;
        _standardError = ReadErrorAsync(process.StandardError);
    }

    private SimulatorHostLaunch(Task<int> host)
    {
        _host = host;
    }

    /// <summary>
    /// Releases the tracked child process handle, if one was started.
    /// </summary>
    public void Dispose() => _process?.Dispose();

    /// <summary>
    /// Creates a launch tracker for a child host process.
    /// </summary>
    /// <param name="process">The spawned host process.</param>
    /// <returns>The launch tracker.</returns>
    internal static SimulatorHostLaunch Create(Process process) => new(process);

    /// <summary>
    /// Creates a launch tracker for an in-process host task.
    /// </summary>
    /// <param name="host">The host task.</param>
    /// <returns>The launch tracker.</returns>
    internal static SimulatorHostLaunch Create(Task<int> host) => new(host);

    /// <summary>
    /// Gets the early host startup failure, if the host has already stopped.
    /// </summary>
    /// <returns>The failure text, or <see langword="null"/> while the host remains active.</returns>
    internal async Task<string?> GetFailureAsync()
    {
        if (_process is not null)
        {
            if (!HasExited(_process))
            {
                return null;
            }

            var error = await _standardError!.ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(error)
                ? "The simulator host stopped before becoming ready."
                : error;
        }

        if (_host is { IsCompleted: true })
        {
            try
            {
                await _host.ConfigureAwait(false);
                return "The simulator host stopped before becoming ready.";
            }
            catch (Exception exception)
            {
                return exception.GetBaseException().Message;
            }
        }

        return null;
    }

    private static bool HasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private static async Task<string> ReadErrorAsync(StreamReader reader)
    {
        var buffer = new char[MaximumErrorCharacters];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(length)).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            length += read;
        }

        return new string(buffer, 0, length).Trim();
    }
}
