using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Hosting;

namespace OssmSharp.Cli.Simulator;

internal sealed class SimulatorBackgroundService : BackgroundService
{
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly string _executablePath;
    private readonly int _port;
    private Process? _process;
    private int _stopping;

    internal SimulatorBackgroundService(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        _executablePath = executablePath;
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        _port = ((IPEndPoint)reservation.LocalEndpoint).Port;
    }

    public string Endpoint => $"tcp://127.0.0.1:{_port}";

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        Volatile.Write(ref _stopping, 1);
        try
        {
            if (_process is { HasExited: false } process)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException) when (process.HasExited)
                {
                }

                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }

            await base.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _process?.Dispose();
        }
    }

    internal Task WaitUntilReadyAsync(CancellationToken cancellationToken) =>
        _ready.Task.WaitAsync(cancellationToken);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var start = new ProcessStartInfo(_executablePath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
            };
            start.ArgumentList.Add("--listen");
            start.ArgumentList.Add($"127.0.0.1:{_port}");
            _process = Process.Start(start)
                ?? throw new IOException("The simulator process could not be started.");

            using var startup = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            startup.CancelAfter(TimeSpan.FromSeconds(5));
            var signal = await _process.StandardOutput.ReadLineAsync(startup.Token).ConfigureAwait(false);
            if (signal != "ready" || _process.HasExited)
            {
                throw new IOException("The simulator did not report a ready listener.");
            }

            _ready.TrySetResult();
            await _process.WaitForExitAsync(stoppingToken).ConfigureAwait(false);
            if (Volatile.Read(ref _stopping) == 0)
                throw new IOException($"The simulator process exited with code {_process.ExitCode}.");
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _ready.TrySetCanceled(stoppingToken);
        }
        catch (OperationCanceledException exception)
        {
            _ready.TrySetException(new TimeoutException("The simulator did not start within five seconds.", exception));
            throw;
        }
        catch (Exception exception)
        {
            _ready.TrySetException(exception);
            throw;
        }
    }

}
