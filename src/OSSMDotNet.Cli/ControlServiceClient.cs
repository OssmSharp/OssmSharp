using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Microsoft.Extensions.Options;
using OSSMDotNet.ControlClient.Clients;
using OSSMDotNet.ControlClient.Models;
using OSSMDotNet.Models;

namespace OSSMDotNet.Cli;

/// <summary>
/// Runs authenticated operator status and stop requests from the command line.
/// </summary>
internal static class ControlServiceClient
{
    /// <summary>
    /// Subscribes to current status or requests Stop from the control service.
    /// </summary>
    /// <param name="origin">The validated control-service origin.</param>
    /// <param name="operation">The status or stop command to execute.</param>
    /// <returns>Zero on a confirmed result; one on missing credentials, rejection, cancellation, or unknown outcome.</returns>
    internal static async Task<int> SendAsync(Uri origin, string operation, CancellationToken cancellationToken)
    {
        var key = Environment.GetEnvironmentVariable("OSSM_CONTROL_CLIENT_SERVICE_KEY");
        var options = new ControlClientOptions { ControlOrigin = origin.GetLeftPart(UriPartial.Authority), ServiceKey = key ?? "" };
        if (!Validator.TryValidateObject(options, new(options), [], validateAllProperties: true))
        {
            await Console.Error.WriteLineAsync("Set OSSM_CONTROL_CLIENT_SERVICE_KEY to a ControlService-authorized service key.");
            return 1;
        }

        await using var client = new OperatorControlClient(new(Options.Create(options)), Options.Create(options));
        var state = new TaskCompletionSource<OperatorLiveControlStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await client.SubscribeAsync(new(value =>
                {
                    state.TrySetResult(value);
                    return Task.CompletedTask;
                }, static _ => Task.CompletedTask,
                resync => Task.FromException(new ControlClientException(resync.Code))), 0, cancellationToken);
            var current = await state.Task.WaitAsync(cancellationToken);
            if (operation == "status")
            {
                await Console.Out.WriteLineAsync(JsonSerializer.Serialize(current));
                return 0;
            }

            await client.StopAsync(cancellationToken);
            await Console.Out.WriteLineAsync("Stop request accepted. Wait for status feedback before treating motion as stopped.");
            return 0;
        }
        catch (ControlClientUnknownOutcomeException)
        {
            await Console.Error.WriteLineAsync("The command outcome is unknown. Do not resend it automatically.");
            return 1;
        }
        catch (ControlClientException error)
        {
            await Console.Error.WriteLineAsync($"Control request rejected: {error.Code}.");
            return 1;
        }
        catch (OperationCanceledException)
        {
            await Console.Error.WriteLineAsync("Control request was cancelled before a confirmed result.");
            return 1;
        }
    }
}
