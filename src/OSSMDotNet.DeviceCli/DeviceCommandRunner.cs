using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OSSMDotNet.Abstractions;
using OSSMDotNet.Simulator;

namespace OSSMDotNet.DeviceCli;

internal static class DeviceCommandRunner
{
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(5);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    internal static Task<int> RunAsync(
        DeviceCommandRequest request,
        CancellationToken cancellationToken) =>
        RunAsync(request, Console.Out, Console.Error, cancellationToken);

    internal static Task<int> RunAsync(
        DeviceCommandRequest request,
        TextWriter output,
        CancellationToken cancellationToken) =>
        RunAsync(request, output, TextWriter.Null, cancellationToken);

    private static async Task<int> RunAsync(
        DeviceCommandRequest request,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        try
        {
            await using var device = new SimulatedDevice(
                Options.Create(new SimulatedDeviceOptions { ExecutablePath = request.SimulatorPath }),
                TimeProvider.System,
                NullLogger<SimulatedDevice>.Instance);
            DeviceState? latestState = null;
            var connectionStarted = false;
            var firstState = new TaskCompletionSource<DeviceState>(TaskCreationOptions.RunContinuationsAsynchronously);
            device.ConnectionStateChanged += state =>
            {
                if (state == DeviceConnectionState.Connecting)
                {
                    connectionStarted = true;
                }
                else if (state == DeviceConnectionState.Disconnected && connectionStarted)
                {
                    firstState.TrySetException(new IOException(
                        "The simulator stopped before publishing its initial state."));
                }
            };
            device.DeviceStateChanged += change =>
            {
                if (change.IsT0)
                {
                    Volatile.Write(ref latestState, change.AsT0);
                    firstState.TrySetResult(change.AsT0);
                }
                else
                {
                    firstState.TrySetException(change.AsT1);
                }
            };
            await device.StartAsync(cancellationToken).ConfigureAwait(false);
            var state = await firstState.Task
                .WaitAsync(OperationTimeout, cancellationToken)
                .ConfigureAwait(false);

            if (request.Operation == DeviceCommandOperation.Move)
            {
                state = state with
                {
                    MotionSettings = state.MotionSettings with
                    {
                        State = MotionDeviceState.StrokeEngineIdle,
                        Speed = request.Speed,
                        Stroke = request.Stroke,
                        Sensation = 50,
                        Depth = request.Depth,
                        Buffer = 0,
                        Pattern = MotionPattern.SimpleStroke,
                    },
                };
                await device.SetMotionAsync(state.MotionSettings, cancellationToken).ConfigureAwait(false);
                state = Volatile.Read(ref latestState)
                    ?? throw new InvalidDataException("The simulator did not publish the applied motion state.");
                await WriteAsync(output, device, state, cancellationToken).ConfigureAwait(false);

                try
                {
                    await Task.Delay(request.Duration, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    await SettleStopAsync(device, state.MotionSettings).ConfigureAwait(false);
                }
            }
            else
            {
                await WriteAsync(output, device, state, cancellationToken).ConfigureAwait(false);
            }

            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            await error.WriteLineAsync(exception.Message).ConfigureAwait(false);
            return 1;
        }
    }

    private static async Task SettleStopAsync(
        SimulatedDevice device,
        MotionSettings motionSettings)
    {
        var stop = device.SetMotionAsync(
            motionSettings with { Speed = 0 },
            CancellationToken.None);
        try
        {
            await stop.WaitAsync(OperationTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException exception)
        {
            await device.StopAsync(CancellationToken.None)
                .WaitAsync(OperationTimeout)
                .ConfigureAwait(false);
            try
            {
                await stop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            throw new TimeoutException("The simulator did not settle the zero-speed command.", exception);
        }
    }

    private static Task WriteAsync(
        TextWriter output,
        SimulatedDevice device,
        DeviceState state,
        CancellationToken cancellationToken) =>
        output.WriteLineAsync(JsonSerializer.Serialize(
            new DeviceCommandOutput(device.ConnectionState, device.Device, state),
            Json).AsMemory(), cancellationToken);
}
