namespace OssmSharp.Cli;

/// <summary>
/// Runs an interactive command session against a connected device.
/// </summary>
internal interface IPromptHandler
{
    /// <summary>
    /// Reads and executes commands until quit, end of input, cancellation, or a connection failure,
    /// then awaits commands already sent.
    /// </summary>
    /// <param name="cancellationToken">Ends the session; commands already sent still complete.</param>
    /// <returns>Zero after a clean exit; one after a device, output, or connection failure.</returns>
    Task<int> RunAsync(CancellationToken cancellationToken = default);
}
