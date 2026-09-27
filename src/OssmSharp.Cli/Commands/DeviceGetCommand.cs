using OssmSharp.Device.Runtime;

namespace OssmSharp.Cli.Commands;

/// <summary>
/// Reads identity details for one specified stock peripheral without issuing motion.
/// </summary>
public sealed class DeviceGetCommand : Command
{
    internal DeviceGetCommand(TextWriter output, TextWriter error)
        : this((_, _) =>
        {
            return ReportUnavailableAsync(error);
        })
    {
    }

    internal DeviceGetCommand(
        Func<string, CancellationToken, Task<string>> readIdentity,
        TextWriter output,
        TextWriter error)
        : this((id, cancellationToken) => RunAsync(
            id, readIdentity, output, error, cancellationToken))
    {
    }

    internal DeviceGetCommand(Func<string, CancellationToken, Task<int>> readIdentity)
        : base("get", "Read a specified stock device's identity without motion writes.")
    {
        ArgumentNullException.ThrowIfNull(readIdentity);
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
        SetAction((parse, cancellationToken) => readIdentity(parse.GetValue(id)!, cancellationToken));
    }

    private static async Task<int> RunAsync(
        string id,
        Func<string, CancellationToken, Task<string>> readIdentity,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(readIdentity);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        try
        {
            var details = await readIdentity(id, cancellationToken).ConfigureAwait(false);
            await output.WriteLineAsync(details.AsMemory(), cancellationToken).ConfigureAwait(false);
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
        catch (Exception exception) when (exception is DirectoryNotFoundException
            or UnauthorizedAccessException or IOException or TimeoutException or InvalidOperationException)
        {
            await error.WriteLineAsync(exception.Message).ConfigureAwait(false);
            return 1;
        }
    }

    private static async Task<int> ReportUnavailableAsync(TextWriter error)
    {
        await error.WriteLineAsync("Stock BLE details require a Windows or macOS native BLE build.")
            .ConfigureAwait(false);
        return 1;
    }
}
