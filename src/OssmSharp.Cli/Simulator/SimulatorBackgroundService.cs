using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Hosting;

namespace OssmSharp.Cli.Simulator;

internal sealed class SimulatorBackgroundService : BackgroundService
{
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly string _executablePath;
    private readonly Func<ProcessStartInfo, Process?> _startProcess;
    private readonly int _port;
    private Process? _process;
    private int _stopping;

    internal SimulatorBackgroundService(string executablePath)
        : this(executablePath, Process.Start)
    {
    }

    internal SimulatorBackgroundService(
        string executablePath,
        Func<ProcessStartInfo, Process?> startProcess)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        _executablePath = executablePath;
        _startProcess = startProcess ?? throw new ArgumentNullException(nameof(startProcess));
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        _port = ((IPEndPoint)reservation.LocalEndpoint).Port;
    }

    public string Endpoint => $"tcp://127.0.0.1:{_port}";

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        Volatile.Write(ref _stopping, 1);
        // A cancelled caller cannot establish that a child has exited. Give owned cleanup
        // its own finite budget, then report an unsettled exit instead of returning success.
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            if (_process is { } process)
                await TerminateProcessAsync(process, cleanup.Token).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                await base.StopAsync(cleanup.Token).ConfigureAwait(false);
            }
            finally
            {
                if (_process is { HasExited: true } exited)
                {
                    _process = null;
                    exited.Dispose();
                }
            }
        }

        if (ExecuteTask is { IsCompleted: false } || _process is { HasExited: false })
            throw new TimeoutException("The owned simulator did not exit during cleanup.");
    }

    internal Task WaitUntilReadyAsync(CancellationToken cancellationToken) =>
        _ready.Task.WaitAsync(cancellationToken);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            stoppingToken.ThrowIfCancellationRequested();
            var start = new ProcessStartInfo(_executablePath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
            };
            start.ArgumentList.Add("--listen");
            start.ArgumentList.Add($"127.0.0.1:{_port}");
            var process = _startProcess(start)
                ?? throw new IOException("The simulator process could not be started.");
            _process = process;

            using var startup = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            startup.CancelAfter(TimeSpan.FromSeconds(5));
            var signal = await process.StandardOutput.ReadLineAsync(startup.Token).ConfigureAwait(false);
            if (signal != "ready" || process.HasExited)
            {
                throw new IOException("The simulator did not report a ready listener.");
            }

            _ready.TrySetResult();
            await process.WaitForExitAsync(stoppingToken).ConfigureAwait(false);
            if (Volatile.Read(ref _stopping) == 0)
                throw new IOException($"The simulator process exited with code {process.ExitCode}.");
        }
        catch (Exception exception)
        {
            if (_ready.Task.IsCompletedSuccessfully)
            {
                if (exception is OperationCanceledException && stoppingToken.IsCancellationRequested)
                    return;

                throw;
            }

            Exception failure = exception is OperationCanceledException && !stoppingToken.IsCancellationRequested
                ? new TimeoutException("The simulator did not start within five seconds.", exception)
                : exception;
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                if (_process is { } process)
                    await TerminateProcessAsync(process, cleanup.Token).ConfigureAwait(false);
            }
            catch (Exception cleanupFailure)
            {
                failure = new AggregateException("Simulator startup and child cleanup failed.", failure, cleanupFailure);
            }

            if (failure is OperationCanceledException && stoppingToken.IsCancellationRequested)
            {
                _ready.TrySetCanceled(stoppingToken);
                return;
            }

            _ready.TrySetException(failure);
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private static async Task TerminateProcessAsync(Process process, CancellationToken cancellationToken)
    {
        if (!process.HasExited)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) when (process.HasExited)
            {
                // The process exited between the state check and the kill attempt.
            }
        }

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
    }
}
