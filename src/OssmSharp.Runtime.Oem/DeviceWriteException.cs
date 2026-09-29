namespace OssmSharp.Runtime.Oem;

/// <summary>
/// Describes a failed primitive write and the known progress of its settings submission.
/// </summary>
/// <remarks>
/// The failed write may also have taken effect. No rollback or physical motion confirmation is implied.
/// </remarks>
public sealed class DeviceWriteException : IOException
{
    /// <summary>
    /// Initializes a failure retaining the provider's original exception.
    /// </summary>
    /// <param name="command">The primitive whose write failed.</param>
    /// <param name="commandsWritten">The number of preceding successful writes.</param>
    /// <param name="innerException">The provider failure.</param>
    public DeviceWriteException(DeviceCommand command, int commandsWritten, Exception innerException)
        : base($"Writing {command.Control} failed after {commandsWritten} completed control writes; settings may be partially applied.", innerException)
    {
        Command = command;
        CommandsWritten = commandsWritten;
    }

    /// <summary>
    /// Gets the command whose result may be unknown.
    /// </summary>
    public DeviceCommand Command { get; }

    /// <summary>
    /// Gets the count of preceding successful writes.
    /// </summary>
    public int CommandsWritten { get; }
}
