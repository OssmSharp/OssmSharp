using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Hosting;
using OneOf;
using OssmSharp.Cli.Models;
using OssmSharp.Cli.Simulator;
using OssmSharp.Device.Primitives;
using OssmSharp.Device.Primitives.Models;
using OssmSharp.Device.Runtime;

namespace OssmSharp.Cli;

/// <summary>
/// Owns the selected foreground device and optional simulator child through cleanup.
/// </summary>
internal sealed class PromptSession
{
    private readonly PromptHandler _prompt;
    private readonly Func<string, SimulatorBackgroundService> _createSimulator;
    private readonly Func<string, IOssmDevice> _createSimulatedDevice;
    private readonly Func<DeviceIdentifier, IOssmDevice>? _createStockDevice;
    private readonly IDeviceProvider? _provider;
    private readonly TimeProvider _timeProvider;
    private readonly TextWriter _error;

    internal PromptSession(
        PromptHandler prompt,
        Func<string, SimulatorBackgroundService> createSimulator,
        Func<string, IOssmDevice> createSimulatedDevice,
        Func<DeviceIdentifier, IOssmDevice>? createStockDevice,
        IDeviceProvider? provider,
        TimeProvider timeProvider,
        TextWriter error)
    {
        _prompt = prompt ?? throw new ArgumentNullException(nameof(prompt));
        _createSimulator = createSimulator ?? throw new ArgumentNullException(nameof(createSimulator));
        _createSimulatedDevice = createSimulatedDevice ?? throw new ArgumentNullException(nameof(createSimulatedDevice));
        _createStockDevice = createStockDevice;
        _provider = provider;
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _error = error ?? throw new ArgumentNullException(nameof(error));
    }

    /// <summary>
    /// Prefers a platform ID over a case-sensitive exact unique device name from the same bounded scan.
    /// </summary>
    internal static DeviceIdentifier ResolveBluetoothIdentifier(
        string selection,
        IReadOnlyList<DeviceIdentifier> candidates)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selection);
        ArgumentNullException.ThrowIfNull(candidates);

        foreach (var candidate in candidates)
        {
            if (string.Equals(candidate.Id, selection, StringComparison.OrdinalIgnoreCase))
                return candidate;
        }

        var matches = candidates
            .Where(candidate => string.Equals(candidate.Name, selection, StringComparison.Ordinal))
            .ToArray();
        if (matches.Length == 1)
            return matches[0];

        if (matches.Length == 0)
            throw new ArgumentException($"Bluetooth device '{DeviceText.Sanitize(selection)}' not found.");

        var options = string.Join(
            ", ",
            matches.Select(candidate =>
                $"{DeviceText.Sanitize(candidate.Name)} ({DeviceText.Sanitize(candidate.Id)})"));
        throw new ArgumentException(
            $"Bluetooth name '{DeviceText.Sanitize(selection)}' is ambiguous. Select a platform ID: {options}.");
    }

    /// <summary>
    /// Waits for a connection and its first reported state before accepting input.
    /// </summary>
    internal static async Task WaitForReadinessAsync(
        IOssmDevice device,
        BackgroundService service,
        Task<bool> connected,
        Task<bool> firstReport,
        Func<Exception?> getFault,
        CancellationToken cancellationToken)
    {
        await service.StartAsync(cancellationToken).ConfigureAwait(false);
        var timeout = Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
        var completed = await Task.WhenAny(
                connected,
                service.ExecuteTask ?? Task.CompletedTask,
                timeout)
            .ConfigureAwait(false);
        if (completed == timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException("Device connection did not become ready within five seconds.");
        }

        if (completed != connected)
        {
            if (service.ExecuteTask is { IsFaulted: true } execution)
                await execution.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            throw new IOException("Device connection ended before readiness.");
        }

        if (!await connected.ConfigureAwait(false) || !IsDeviceConnected(device))
        {
            throw new IOException("Device connection failed for the selected device.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        completed = await Task.WhenAny(
                firstReport,
                service.ExecuteTask ?? Task.CompletedTask,
                timeout)
            .ConfigureAwait(false);
        if (completed == timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException("Device did not report initial feedback within five seconds.");
        }

        if (completed != firstReport)
        {
            if (service.ExecuteTask is { IsFaulted: true } execution)
                await execution.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            throw new IOException("Device disconnected before initial feedback.");
        }

        if (!await firstReport.ConfigureAwait(false))
            throw new IOException("Device feedback failed before readiness.", getFault());
        if (!IsDeviceConnected(device))
            throw new IOException("Device disconnected during initial feedback.");
        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>
    /// Selects, connects, runs the prompt and disposes owned resources.
    /// </summary>
    internal async Task<int> RunAsync(PromptSelection selection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selection);

        IOssmDevice? device = null;
        SimulatorBackgroundService? child = null;
        DeviceExecutionSession? execution = null;
        Action<DeviceConnectionState>? onConnectionChanged = null;
        Action<OneOf<DeviceState, Exception>>? onStateChanged = null;
        Task<int>? activePrompt = null;

        try
        {
            switch (selection.Mode)
            {
                case PromptMode.Bluetooth:
                    if (_createStockDevice is null || _provider is null)
                    {
                        throw new InvalidOperationException(
                            "Stock BLE connection unavailable: use a native BLE build.");
                    }

                    var candidates = await _provider.GetAvailableDevicesAsync(cancellationToken).ConfigureAwait(false);
                    var selected = ResolveBluetoothIdentifier(selection.Value, candidates);
                    device = _createStockDevice(selected);
                    break;

                case PromptMode.SimulatorLaunch:
                    child = _createSimulator(selection.Value);
                    await child.StartAsync(cancellationToken).ConfigureAwait(false);
                    await child.WaitUntilReadyAsync(cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (child.ExecuteTask?.IsCompleted == true)
                    {
                        await child.ExecuteTask.ConfigureAwait(false);
                        throw new IOException("The simulator process exited before device connection.");
                    }

                    device = _createSimulatedDevice(child.Endpoint);
                    break;

                case PromptMode.SimulatorAttach:
                    device = _createSimulatedDevice(selection.Value);
                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(selection), "Unknown device selection mode.");
            }

            var selectedDevice = device
                ?? throw new InvalidOperationException("The selected session has no device.");
            var deviceExecution = CreateDeviceExecutionSession(selectedDevice);
            execution = deviceExecution;

            if (selectedDevice is not BackgroundService service)
                throw new InvalidOperationException("The selected device cannot start a foreground connection.");

            var connected = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var firstReport = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            onConnectionChanged = state =>
            {
                if (state == DeviceConnectionState.Connected)
                    connected.TrySetResult(true);
                else if (state == DeviceConnectionState.Disconnected)
                    connected.TrySetResult(false);
            };
            onStateChanged = change =>
                firstReport.TrySetResult(change.IsT0);
            selectedDevice.ConnectionStateChanged += onConnectionChanged;
            selectedDevice.DeviceStateChanged += onStateChanged;

            await WaitForReadinessAsync(
                    selectedDevice,
                    service,
                    connected.Task,
                    firstReport.Task,
                    () => deviceExecution.Fault,
                    cancellationToken)
                .ConfigureAwait(false);

            if (selection.Mode != PromptMode.Bluetooth)
            {
                var authorityRevision = deviceExecution.AuthorityRevision;
                await deviceExecution.InitializeAsync(authorityRevision, cancellationToken).ConfigureAwait(false);
            }

            return await RunPromptAsync(
                    selectedDevice,
                    service,
                    child,
                    selection.Mode != PromptMode.Bluetooth,
                    deviceExecution,
                    task => activePrompt = task,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            await _error.WriteLineAsync($"Device connection failed: {DeviceText.Sanitize(exception.Message)}")
                .ConfigureAwait(false);
            return 1;
        }
        finally
        {
            await CleanupOwnedResourcesAsync(
                    device,
                    child,
                    execution,
                    onConnectionChanged,
                    onStateChanged,
                    activePrompt)
                .ConfigureAwait(false);
        }
    }

    private static bool IsDeviceConnected(IOssmDevice device) =>
        device.ConnectionState == DeviceConnectionState.Connected;

    private static async Task AwaitCleanupWithinBudgetAsync(Task cleanup, CancellationToken cancellationToken)
    {
        try
        {
            await cleanup.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _ = cleanup.ContinueWith(
                static task => _ = task.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            throw new TimeoutException("Device cleanup did not settle within five seconds.");
        }
    }

    private async Task<int> RunPromptAsync(
        IOssmDevice device,
        BackgroundService service,
        SimulatorBackgroundService? child,
        bool simulatorControl,
        DeviceExecutionSession execution,
        Action<Task<int>> recordPrompt,
        CancellationToken cancellationToken)
    {
        using var promptCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var prompt = _prompt.RunAsync(
            device,
            () => execution.Reported,
            () => execution.Fault,
            promptCancellation.Token,
            simulatorControl,
            execution: execution);
        recordPrompt(prompt);

        var deviceTask = service.ExecuteTask ?? new TaskCompletionSource().Task;
        var childTask = child?.ExecuteTask;
        var first = childTask is null
            ? await Task.WhenAny(prompt, deviceTask).ConfigureAwait(false)
            : await Task.WhenAny(prompt, deviceTask, childTask).ConfigureAwait(false);
        if (first == prompt)
        {
            var result = await prompt.ConfigureAwait(false);
            if (childTask?.IsCompleted == true || deviceTask.IsCompleted)
                throw new IOException("The selected device or simulator exited during the prompt.");
            return result;
        }

        await promptCancellation.CancelAsync().ConfigureAwait(false);
        using (var settleTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        {
            try
            {
                await prompt.WaitAsync(settleTimeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (settleTimeout.IsCancellationRequested)
            {
                throw new TimeoutException("The prompt did not settle after connection loss.");
            }
            catch (OperationCanceledException) when (promptCancellation.IsCancellationRequested)
            {
                // Connection loss ended the prompt; still await it before disposing its device.
            }
        }

        if (first.IsFaulted)
            await first.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        throw new IOException(
            first == childTask
                ? "The simulator process exited before the prompt finished."
                : "The selected device disconnected before the prompt finished.");
    }

    private async ValueTask CleanupOwnedResourcesAsync(
        IOssmDevice? device,
        SimulatorBackgroundService? child,
        DeviceExecutionSession? execution,
        Action<DeviceConnectionState>? onConnectionChanged,
        Action<OneOf<DeviceState, Exception>>? onStateChanged,
        Task<int>? activePrompt)
    {
        var failures = new List<Exception>();
        using var cleanupBudget = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var cleanupToken = cleanupBudget.Token;
        var executionDisposalFailed = false;

        if (device is not null)
        {
            try
            {
                if (execution is not null && device.ConnectionState == DeviceConnectionState.Connected)
                    await execution.StopAsync(cleanupToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }

            try
            {
                if (execution is not null)
                {
                    await AwaitCleanupWithinBudgetAsync(
                            execution.DisposeAsync().AsTask(),
                            cleanupToken)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception exception)
            {
                failures.Add(exception);
                executionDisposalFailed = true;
            }

            if (!executionDisposalFailed)
            {
                try
                {
                    await AwaitCleanupWithinBudgetAsync(
                            device.DisposeAsync().AsTask(),
                            cleanupToken)
                        .ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    failures.Add(exception);
                }
            }

            if (onConnectionChanged is not null)
                device.ConnectionStateChanged -= onConnectionChanged;
            if (onStateChanged is not null)
                device.DeviceStateChanged -= onStateChanged;
        }

        if (child is not null)
        {
            try
            {
                await child.StopAsync(cleanupToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
            finally
            {
                child.Dispose();
            }
        }

        if (activePrompt is { IsCompleted: false })
            failures.Add(new TimeoutException("The prompt did not settle during cleanup."));

        if (failures.Count > 0)
            await _error.WriteLineAsync("Device cleanup did not settle; Stop is unconfirmed.").ConfigureAwait(false);
        if (failures.Count > 1)
            throw new AggregateException("Device session cleanup did not settle.", failures);
        if (failures.Count == 1)
            ExceptionDispatchInfo.Capture(failures[0]).Throw();
    }

    private DeviceExecutionSession CreateDeviceExecutionSession(IOssmDevice device) =>
        new(device, _timeProvider);

}
