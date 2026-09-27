using OssmSharp.Device.Primitives;

namespace OssmSharp.Cli.Commands;

/// <summary>
/// Lists bounded stock-device discovery results.
/// </summary>
public sealed class DeviceListCommand : Command
{
    private readonly IDeviceDiscovery _discovery;
    private readonly TextWriter _output;
    private readonly TextWriter _error;

    internal DeviceListCommand(
        IDeviceDiscovery discovery,
        TextWriter output,
        TextWriter error)
        : base("list", "List stock devices and their platform peripheral IDs without connecting.")
    {
        _discovery = discovery ?? throw new ArgumentNullException(nameof(discovery));
        _output = output ?? throw new ArgumentNullException(nameof(output));
        _error = error ?? throw new ArgumentNullException(nameof(error));
        SetAction((_, cancellationToken) => RunAsync(cancellationToken));
    }

    internal async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _output.WriteLineAsync("Detecting devices. Please wait ...");
            var candidates = await _discovery.ScanAsync(cancellationToken).ConfigureAwait(false);
            if (candidates.Count == 0)
            {
                await _output.WriteLineAsync("No devices found.").ConfigureAwait(false);
            }
            else
            {
                for (var index = 0; index < candidates.Count; index++)
                {
                    var candidate = candidates[index];
                    var name = string.IsNullOrWhiteSpace(candidate.Name)
                        ? "Unnamed device"
                        : DeviceText.Sanitize(candidate.Name);
                    var id = DeviceText.Sanitize(candidate.Id);
                    await _output.WriteLineAsync($"{index + 1}. {name} ({id})".AsMemory(), cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return 130;
        }
        catch (Exception exception) when (exception is DirectoryNotFoundException or UnauthorizedAccessException
            or IOException or TimeoutException or PlatformNotSupportedException)
        {
            await _error.WriteLineAsync(exception.Message).ConfigureAwait(false);
            return 1;
        }
    }
}
