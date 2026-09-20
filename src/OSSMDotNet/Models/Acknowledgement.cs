using System.Diagnostics.CodeAnalysis;

namespace OSSMDotNet.Models;

internal sealed record Acknowledgement
{
    private const string Prefix = "ok:";

    public Acknowledgement(string command)
    {
        ArgumentException.ThrowIfNullOrEmpty(command);
        Command = command;
    }

    public string Command { get; }

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
