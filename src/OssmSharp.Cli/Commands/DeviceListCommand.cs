using OssmSharp.Device.Runtime;
#if WINDOWS || MACOS
using OssmSharp.Device.Stock;
using Shiny.BluetoothLE;
#endif

namespace OssmSharp.Cli.Commands;

/// <summary>
/// Lists bounded stock-device discovery results while holding device ownership.
/// </summary>
public sealed class DeviceListCommand : Command
{
    private readonly Func<IDisposable?> _acquireOwnership;
    private readonly Func<CancellationToken, Task<IReadOnlyList<(string Name, string Id)>>> _scan;
    private readonly TextWriter _output;
    private readonly TextWriter _error;

#if WINDOWS || MACOS
    internal DeviceListCommand(IBleManager manager)
        : this(
            DeviceOwnershipLock.TryAcquire,
            cancellationToken => StockBleDiscovery.ScanAsync(manager, cancellationToken),
            Console.Out,
            Console.Error)
    {
    }
#endif

    internal DeviceListCommand(
        Func<IDisposable?> acquireOwnership,
        Func<CancellationToken, Task<IReadOnlyList<(string Name, string Id)>>> scan,
        TextWriter output,
        TextWriter error)
        : base("list", "List stock devices and their platform peripheral IDs without connecting.")
    {
        _acquireOwnership = acquireOwnership ?? throw new ArgumentNullException(nameof(acquireOwnership));
        _scan = scan ?? throw new ArgumentNullException(nameof(scan));
        _output = output ?? throw new ArgumentNullException(nameof(output));
        _error = error ?? throw new ArgumentNullException(nameof(error));
        SetAction((_, cancellationToken) => RunAsync(cancellationToken));
    }

    internal async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var lease = _acquireOwnership();
            if (lease is null)
            {
                await _error.WriteLineAsync("Device is already owned by another process.").ConfigureAwait(false);
                return 1;
            }

            var candidates = await _scan(cancellationToken).ConfigureAwait(false);
            if (candidates.Count == 0)
            {
                await _output.WriteLineAsync("No stock devices found.").ConfigureAwait(false);
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
