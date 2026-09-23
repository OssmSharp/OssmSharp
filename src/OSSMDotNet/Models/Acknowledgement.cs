using System.Diagnostics.CodeAnalysis;

namespace OSSMDotNet.Models;

/// <summary>
/// Represents a stock-firmware acknowledgement for a submitted setting command.
/// </summary>
internal sealed record Acknowledgement
{
    private const string Prefix = "ok:";

    /// <summary>
    /// Creates an acknowledgement for a nonempty stock-firmware command.
    /// </summary>
    /// <param name="command">The acknowledged setting command.</param>
    /// <exception cref="ArgumentNullException">The command is null.</exception>
    /// <exception cref="ArgumentException">The command is empty.</exception>
    public Acknowledgement(string command)
    {
        ArgumentException.ThrowIfNullOrEmpty(command);
        Command = command;
    }

    /// <summary>
    /// Gets the command reported by the firmware.
    /// </summary>
    public string Command { get; }

    /// <summary>
    /// Checks whether an acknowledgement exactly matches the expected setting command.
    /// </summary>
    /// <param name="payload">The reported acknowledgement, if any.</param>
    /// <param name="expectedRequest">The command whose acknowledgement is expected.</param>
    /// <param name="acknowledgement">The matching acknowledgement, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> only when the acknowledgement matches the submitted command.</returns>
    public static bool TryParse(
        string? payload,
        Request expectedRequest,
        [NotNullWhen(true)] out Acknowledgement? acknowledgement)
    {
        ArgumentNullException.ThrowIfNull(expectedRequest);

        if (payload is null ||
            !payload.StartsWith(Prefix, StringComparison.Ordinal) ||
            payload.Length == Prefix.Length)
        {
            acknowledgement = null;
            return false;
        }

        var parsed = new Acknowledgement(payload[Prefix.Length..]);
        if (parsed != new Acknowledgement(expectedRequest.ToPayload()))
        {
            acknowledgement = null;
            return false;
        }

        acknowledgement = parsed;
        return true;
    }
}
