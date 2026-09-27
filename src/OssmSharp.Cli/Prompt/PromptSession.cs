using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Hosting;
using OneOf;
using OssmSharp.Cli.Models;
using OssmSharp.Cli.Simulator;
using OssmSharp.Device.Primitives;
using OssmSharp.Device.Runtime;

namespace OssmSharp.Cli;

/// <summary>
/// Owns the selected foreground device, optional simulator child, and machine lease through cleanup.
/// </summary>
internal sealed class PromptSession
{
    private readonly PromptHandler _prompt;
    private readonly Func<IDisposable?> _acquireOwnership;
    private readonly Func<string, SimulatorBackgroundService> _createSimulator;
    private readonly Func<string, IOssmDevice> _createSimulatedDevice;
    private readonly Func<string, IOssmDevice>? _createStockDevice;
    private readonly Func<CancellationToken, Task<IReadOnlyList<(string Name, string Id)>>>? _scan;
    private readonly TextWriter _error;

    internal PromptSession(
        PromptHandler prompt,
        Func<IDisposable?> acquireOwnership,
        Func<string, SimulatorBackgroundService> createSimulator,
        Func<string, IOssmDevice> createSimulatedDevice,
        Func<string, IOssmDevice>? createStockDevice,
        Func<CancellationToken, Task<IReadOnlyList<(string Name, string Id)>>>? scan,
        TextWriter error)
    {
        _prompt = prompt ?? throw new ArgumentNullException(nameof(prompt));
        _acquireOwnership = acquireOwnership ?? throw new ArgumentNullException(nameof(acquireOwnership));
        _createSimulator = createSimulator ?? throw new ArgumentNullException(nameof(createSimulator));
        _createSimulatedDevice = createSimulatedDevice ?? throw new ArgumentNullException(nameof(createSimulatedDevice));
        _createStockDevice = createStockDevice;
        _scan = scan;
        _error = error ?? throw new ArgumentNullException(nameof(error));
    }

    /// <summary>
    /// Prefers a platform ID over a case-sensitive exact unique device name from the same bounded scan.
    /// </summary>
    internal static string ResolveBluetoothId(
        string selection,
        IReadOnlyList<(string Name, string Id)> candidates)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selection);
        ArgumentNullException.ThrowIfNull(candidates);

        foreach (var candidate in candidates)
        {
            if (string.Equals(candidate.Id, selection, StringComparison.OrdinalIgnoreCase))
                return candidate.Id;
        }

        var matches = candidates
            .Where(candidate => string.Equals(candidate.Name, selection, StringComparison.Ordinal))
            .ToArray();
        if (matches.Length == 1)
            return matches[0].Id;

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
    /// Waits for connection and, for simulation, the first observed state before accepting input.
    /// </summary>
    internal static async Task WaitForReadinessAsync(
        IOssmDevice device,
        BackgroundService service,
        Task<bool> connected,
        Task<bool> firstReport,
        bool simulatorControl,
        Func<Exception?> getFault,
        CancellationToken cancellationToken)
    {
        await service.StartAsync(cancellationToken).ConfigureAwait(false);
        using var readiness = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        readiness.CancelAfter(TimeSpan.FromSeconds(5));
        var timeout = Task.Delay(Timeout.InfiniteTimeSpan, readiness.Token);
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

        if (!await connected.ConfigureAwait(false)
            || device.ConnectionState != DeviceConnectionState.Connected)
        {
            throw new IOException("Device connection failed for the selected device.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!simulatorControl)
            return;

        completed = await Task.WhenAny(
                firstReport,
                service.ExecuteTask ?? Task.CompletedTask,
                timeout)
            .ConfigureAwait(false);
        if (completed == timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException("Simulator did not report initial feedback within five seconds.");
        }

        if (completed != firstReport)
        {
            if (service.ExecuteTask is { IsFaulted: true } execution)
                await execution.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            throw new IOException("Simulator disconnected before initial feedback.");
        }

        if (!await firstReport.ConfigureAwait(false))
            throw new IOException("Simulator feedback failed before readiness.", getFault());
        if (device.ConnectionState != DeviceConnectionState.Connected)
            throw new IOException("Simulator disconnected during initial feedback.");
        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>
    /// Holds one lease while selecting, connecting, running the prompt and disposing owned resources.
    /// </summary>
    internal async Task<int> RunAsync(PromptSelection selection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selection);

        IDisposable? lease;
        try
        {
            lease = _acquireOwnership();
        }
        catch (Exception exception) when (exception is DirectoryNotFoundException
            or UnauthorizedAccessException
            or IOException)
        {
            await _error.WriteLineAsync(exception.Message).ConfigureAwait(false);
            return 1;
        }

        if (lease is null)
        {
            await _error.WriteLineAsync("Device is already owned by another process.").ConfigureAwait(false);
            return 1;
        }

        IOssmDevice? device = null;
        SimulatorBackgroundService? child = null;
        Action<DeviceConnectionState>? onConnectionChanged = null;
        Action<OneOf<DeviceState, Exception>>? onStateChanged = null;
        DeviceState? reported = null;
        MotionSettings? submitted = null;
        Exception? fault = null;
        Task<int>? activePrompt = null;

        ValueTask CleanupAsync() => CleanupOwnedResourcesAsync(
            selection.Mode,
            device,
            child,
            onConnectionChanged,
            onStateChanged,
            submitted,
            Volatile.Read(ref reported),
            activePrompt);

        try
        {
            switch (selection.Mode)
            {
                case PromptMode.Bluetooth:
                    if (_scan is null || _createStockDevice is null)
                    {
                        throw new InvalidOperationException(
                            "Stock BLE connection unavailable: use a native BLE build.");
                    }

                    var candidates = await _scan(cancellationToken).ConfigureAwait(false);
                    var selectedId = ResolveBluetoothId(selection.Value, candidates);
                    device = _createStockDevice(selectedId);
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

            if (device is not BackgroundService service)
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
            {
                if (change.IsT0)
                {
                    Volatile.Write(ref reported, change.AsT0);
                    firstReport.TrySetResult(true);
                }
                else
                {
                    Volatile.Write(ref fault, change.AsT1);
                    if (selection.Mode != PromptMode.Bluetooth)
                        firstReport.TrySetResult(false);
                }
            };
            device.ConnectionStateChanged += onConnectionChanged;
            device.DeviceStateChanged += onStateChanged;

            await WaitForReadinessAsync(
                    device,
                    service,
                    connected.Task,
                    firstReport.Task,
                    selection.Mode != PromptMode.Bluetooth,
                    () => Volatile.Read(ref fault),
                    cancellationToken)
                .ConfigureAwait(false);

            return await RunPromptAsync(
                    device,
                    service,
                    child,
                    selection.Mode != PromptMode.Bluetooth,
                    () => Volatile.Read(ref reported),
                    () => Volatile.Read(ref fault),
                    settings => submitted = settings,
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
            if (lease is DeviceOwnershipLock machineLease)
                await machineLease.ReleaseAfterAsync(CleanupAsync).ConfigureAwait(false);
            else
            {
                await CleanupAsync().ConfigureAwait(false);
                lease.Dispose();
            }
        }
    }

    private async Task<int> RunPromptAsync(
        IOssmDevice device,
        BackgroundService service,
        SimulatorBackgroundService? child,
        bool simulatorControl,
        Func<DeviceState?> getReported,
        Func<Exception?> getFault,
        Action<MotionSettings> recordSubmission,
        Action<Task<int>> recordPrompt,
        CancellationToken cancellationToken)
    {
        using var promptCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var prompt = _prompt.RunAsync(
            device,
            getReported,
            getFault,
            promptCancellation.Token,
            simulatorControl,
            recordSubmission);
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
        PromptMode mode,
        IOssmDevice? device,
        SimulatorBackgroundService? child,
        Action<DeviceConnectionState>? onConnectionChanged,
        Action<OneOf<DeviceState, Exception>>? onStateChanged,
        MotionSettings? submitted,
        DeviceState? reported,
        Task<int>? activePrompt)
    {
        var failures = new List<Exception>();

        if (device is not null)
        {
            try
            {
                if (mode != PromptMode.Bluetooth && device.ConnectionState == DeviceConnectionState.Connected)
                {
                    using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await device.SetMotionAsync(
                            PromptHandler.StoppedSettings(submitted ?? reported?.MotionSettings),
                            stopTimeout.Token)
                        .WaitAsync(stopTimeout.Token)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }

            try
            {
                await device.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
            finally
            {
                if (onConnectionChanged is not null)
                    device.ConnectionStateChanged -= onConnectionChanged;
                if (onStateChanged is not null)
                    device.DeviceStateChanged -= onStateChanged;
            }
        }

        if (child is not null)
        {
            try
            {
                using var childTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await child.StopAsync(childTimeout.Token).ConfigureAwait(false);
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
            await _error.WriteLineAsync("Device cleanup did not settle; ownership is retained.").ConfigureAwait(false);
        if (failures.Count > 1)
            throw new AggregateException("Device session cleanup did not settle.", failures);
        if (failures.Count == 1)
            ExceptionDispatchInfo.Capture(failures[0]).Throw();
    }

}
