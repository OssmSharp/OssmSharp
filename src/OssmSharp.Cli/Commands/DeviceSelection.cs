using System.Globalization;

namespace OssmSharp.Cli.Commands;

internal static class DeviceSelection
{
    internal static async Task<string> SelectAsync(
        IReadOnlyList<(string Name, string Id)> candidates,
        TextReader input,
        TextWriter prompt,
        bool isInteractive,
        CancellationToken cancellationToken)
    {
        if (!isInteractive)
        {
            throw new InvalidOperationException("Interactive selection requires a terminal. Supply --peripheral-id instead.");
        }

        if (candidates.Count == 0)
        {
            throw new InvalidOperationException("No stock devices were found. Try again or supply --peripheral-id.");
        }

        for (var index = 0; index < candidates.Count; index++)
        {
            var name = candidates[index].Name;
            var safeName = string.IsNullOrWhiteSpace(name)
                ? "Unnamed device"
                : string.Concat(name.Select(character => char.IsControl(character) ? ' ' : character));
            await prompt.WriteLineAsync(
                $"{index + 1}. {safeName} ({candidates[index].Id})".AsMemory(), cancellationToken)
                .ConfigureAwait(false);
        }

        await prompt.WriteAsync("Select a device by number and press Enter: ".AsMemory(), cancellationToken)
            .ConfigureAwait(false);
        await prompt.FlushAsync(cancellationToken).ConfigureAwait(false);
        var response = await input.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (!int.TryParse(response, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ||
            number < 1 || number > candidates.Count)
        {
            throw new ArgumentException($"Enter a device number from 1 to {candidates.Count}.");
        }

        return candidates[number - 1].Id;
    }
}
