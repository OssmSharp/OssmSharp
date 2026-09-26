using System.Text.Json;

namespace OssmSharp.Cli.Commands;

/// <summary>
/// Sends diagnostic commands to the current user's persistent simulator host.
/// </summary>
internal static class DeviceCommandRunner
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(5);
    private static readonly JsonSerializerOptions OutputJson = new(SimulatorIpcProtocol.SerializerOptions)
    {
        WriteIndented = true,
    };

    /// <summary>
    /// Executes a command using the default console streams and host endpoint.
    /// </summary>
    /// <param name="request">The parsed command to execute.</param>
    /// <returns>Zero on success; one on host or command failure.</returns>
    internal static Task<int> RunAsync(
        DeviceCommandRequest request,
        CancellationToken cancellationToken) =>
        RunAsync(
            request,
            Console.Out,
            Console.Error,
            SimulatorEndpoint.CreateDefault(),
            SimulatorProcessLauncher.Start,
            cancellationToken);

    /// <summary>
    /// Executes a command using a supplied output writer and the default host endpoint.
    /// </summary>
    /// <param name="request">The parsed command to execute.</param>
    /// <param name="output">The writer for successful command output.</param>
    /// <returns>Zero on success; one on host or command failure.</returns>
    internal static Task<int> RunAsync(
        DeviceCommandRequest request,
        TextWriter output,
        CancellationToken cancellationToken) =>
        RunAsync(
            request,
            output,
            TextWriter.Null,
            SimulatorEndpoint.CreateDefault(),
            SimulatorProcessLauncher.Start,
            cancellationToken);

    /// <summary>
    /// Executes a command against a supplied host endpoint and launcher.
    /// </summary>
    /// <param name="request">The parsed command to execute.</param>
    /// <param name="output">The writer for successful command output.</param>
    /// <param name="error">The writer for command failures.</param>
    /// <param name="endpoint">The private endpoint of the simulator host.</param>
    /// <param name="startHost">The operation that starts a host when required.</param>
    /// <returns>Zero on success; one on host or command failure.</returns>
    internal static async Task<int> RunAsync(
        DeviceCommandRequest request,
        TextWriter output,
        TextWriter error,
        SimulatorEndpoint endpoint,
        Func<string, SimulatorEndpoint, SimulatorHostLaunch> startHost,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(startHost);

        try
        {
            if (request.Operation == DeviceCommandOperation.Identify)
            {
#if MACOS
                var identity = await StockIdentityReader.ReadAsync(
                    request.PeripheralId,
                    Console.In,
                    error,
                    !Console.IsInputRedirected && !Console.IsErrorRedirected,
                    cancellationToken)
                    .ConfigureAwait(false);
                await output.WriteLineAsync(identity.AsMemory(), cancellationToken).ConfigureAwait(false);
                return 0;
#else
                await error.WriteLineAsync("Read-only BLE identification requires the macOS-targeted CLI build.")
                    .ConfigureAwait(false);
                return 1;
#endif
            }

            var ipcRequest = ToIpcRequest(request);
            var response = request.Operation == DeviceCommandOperation.Start
                ? await StartAsync(
                    request.SimulatorPath!,
                    ipcRequest,
                    endpoint,
                    startHost,
                    cancellationToken).ConfigureAwait(false)
                : await SimulatorClient.SendAsync(endpoint, ipcRequest, cancellationToken)
                    .ConfigureAwait(false);

            if (!response.Ok)
            {
                await error.WriteLineAsync(response.Error ?? "The simulator host rejected the command.")
                    .ConfigureAwait(false);
                return 1;
            }

            if (response.Output is not null)
            {
                await output.WriteLineAsync(
                    JsonSerializer.Serialize(response.Output, OutputJson).AsMemory(),
                    cancellationToken).ConfigureAwait(false);
            }

            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            request.Operation != DeviceCommandOperation.Identify &&
            exception is IOException or TimeoutException)
        {
            await error.WriteLineAsync(
                "The simulator host is not running. Run 'start --simulator-path <path>' first.")
                .ConfigureAwait(false);
            return 1;
        }
        catch (Exception exception)
        {
            await error.WriteLineAsync(exception.Message).ConfigureAwait(false);
            return 1;
        }
    }

    private static async Task<SimulatorIpcResponse> StartAsync(
        string simulatorPath,
        SimulatorIpcRequest request,
        SimulatorEndpoint endpoint,
        Func<string, SimulatorEndpoint, SimulatorHostLaunch> startHost,
        CancellationToken cancellationToken)
    {
        try
        {
            return await SimulatorClient.SendAsync(endpoint, request, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or TimeoutException)
        {
            using var host = startHost(simulatorPath, endpoint);

            return await WaitForHostAsync(host, request, endpoint, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<SimulatorIpcResponse> WaitForHostAsync(
        SimulatorHostLaunch host,
        SimulatorIpcRequest request,
        SimulatorEndpoint endpoint,
        CancellationToken cancellationToken)
    {
        var deadline = TimeProvider.System.GetTimestamp() +
            (long)(StartupTimeout.TotalSeconds * TimeProvider.System.TimestampFrequency);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await SimulatorClient.SendAsync(endpoint, request, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is IOException or TimeoutException &&
                TimeProvider.System.GetTimestamp() < deadline)
            {
                var failure = await host.GetFailureAsync().ConfigureAwait(false);
                if (failure is not null)
                {
                    throw new InvalidOperationException(failure, exception);
                }

                await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static SimulatorIpcRequest ToIpcRequest(DeviceCommandRequest request) => request.Operation switch
    {
        DeviceCommandOperation.Start => new("start", request.SimulatorPath),
        DeviceCommandOperation.Status => new("status"),
        DeviceCommandOperation.Move => new(
            "move",
            Speed: request.Speed,
            Stroke: request.Stroke,
            Depth: request.Depth,
            DurationMilliseconds: checked((int)request.Duration.TotalMilliseconds)),
        DeviceCommandOperation.Stop => new("stop"),
        _ => throw new ArgumentOutOfRangeException(nameof(request)),
    };
}
