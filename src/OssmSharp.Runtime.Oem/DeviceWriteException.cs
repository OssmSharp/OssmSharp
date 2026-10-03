using OssmSharp.Runtime.Oem.Protocol;

namespace OssmSharp.Runtime.Oem;

/// <summary>
/// Describes a failed settings request and its known partial progress.
/// </summary>
/// <remarks>
/// The failed write may also have taken effect. No rollback is implied.
/// </remarks>
public sealed class DeviceWriteException : IOException
{
    /// <summary>
    /// Initializes a failure retaining the provider's original exception.
    /// </summary>
    /// <param name="commandsWritten">The number of preceding successful writes.</param>
    /// <param name="innerException">The provider failure.</param>
    public DeviceWriteException(int commandsWritten, Exception innerException)
        : base("Sending settings failed; settings may be partially applied.", innerException)
    {
        CommandsWritten = commandsWritten;
    }

    internal DeviceWriteException(DeviceCommand command, int commandsWritten, Exception innerException)
        : this(commandsWritten, innerException)
    {
        Command = command;
    }

    /// <summary>
    /// Gets the count of preceding successful writes.
    /// </summary>
    public int CommandsWritten { get; }

    internal DeviceCommand Command { get; }
}
