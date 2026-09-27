using OssmSharp.Device.Primitives;
using OssmSharp.Device.Primitives.Models;

namespace OssmSharp.Cli.Commands;

/// <summary>
/// Reads identity details for one specified stock peripheral without issuing motion.
/// </summary>
public sealed class DeviceGetCommand : Command
{
    internal DeviceGetCommand(
        IDeviceDiscovery discovery,
        IDeviceProvider provider,
        TextWriter output,
        TextWriter error)
        : base("get", "Read a specified stock device's identity without motion writes.")
    {
        ArgumentNullException.ThrowIfNull(discovery);
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        var id = new Argument<string>("id")
        {
            Arity = ArgumentArity.ExactlyOne,
            Description = "Platform-specific ID of the stock device to read.",
        };
        id.Validators.Add(result =>
        {
            if (string.IsNullOrWhiteSpace(result.GetValue(id)))
            {
                result.AddError("Provide one nonempty platform device ID.");
            }
        });
        Arguments.Add(id);
        SetAction((parse, cancellationToken) => RunAsync(
            parse.GetValue(id)!,
            discovery,
            provider,
            output,
            error,
            cancellationToken));
    }

    private static async Task<int> RunAsync(
        string id,
        IDeviceDiscovery discovery,
        IDeviceProvider provider,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        try
        {
            var candidates = await discovery.ScanAsync(cancellationToken).ConfigureAwait(false);
            var selected = candidates.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, id, StringComparison.OrdinalIgnoreCase));
            if (selected is null)
            {
                throw new InvalidOperationException(
                    $"No stock device with platform ID '{DeviceText.Sanitize(id)}' was discovered.");
            }

            var details = await provider.ReadInformationAsync(selected, cancellationToken).ConfigureAwait(false);
            await output.WriteLineAsync(Format(details).AsMemory(), cancellationToken).ConfigureAwait(false);
            return 0;
        }
        catch (UnsettledDeviceCleanupException exception)
        {
            await error.WriteLineAsync(exception.Message).ConfigureAwait(false);
            return 1;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return 130;
        }
        catch (PlatformNotSupportedException)
        {
            await error.WriteLineAsync("Stock BLE details require a Windows or macOS native BLE build.")
                .ConfigureAwait(false);
            return 1;
        }
        catch (Exception exception) when (exception is DirectoryNotFoundException
            or UnauthorizedAccessException or IOException or TimeoutException or InvalidOperationException
            or ArgumentException)
        {
            await error.WriteLineAsync(exception.Message).ConfigureAwait(false);
            return 1;
        }
    }

    private static string Format(DeviceIdentityDetails details)
    {
        var identifier = details.Identifier;
        var name = string.IsNullOrWhiteSpace(identifier.Name)
            ? string.Empty
            : $"Name: {DeviceText.Sanitize(identifier.Name)}{Environment.NewLine}";
        return $"Peripheral ID: {DeviceText.Sanitize(identifier.Id)}{Environment.NewLine}{name}"
            + $"Wi-Fi MAC: {DeviceText.Sanitize(details.WifiMac ?? string.Empty)}{Environment.NewLine}"
            + $"Chip: {DeviceText.Sanitize(details.Chip ?? string.Empty)}{Environment.NewLine}"
            + $"Wi-Fi connected: {details.WifiConnected?.ToString() ?? string.Empty}{Environment.NewLine}"
            + $"Sketch MD5: {DeviceText.Sanitize(details.SketchMd5 ?? string.Empty)}{Environment.NewLine}"
            + $"Firmware: {DeviceText.Sanitize(details.FirmwareVersion ?? string.Empty)}";
    }
}
