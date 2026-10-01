using Microsoft.Extensions.Hosting;
using System.Runtime.InteropServices;

namespace OssmSharp.Cli;

internal sealed class CommandHostLifetime(
    IHostApplicationLifetime applicationLifetime,
    PromptInterruptState interruptState) : IHostLifetime, IDisposable
{
    private PosixSignalRegistration? terminationRegistration;
    private bool listening;

    public void Dispose()
    {
        if (listening)
        {
            Console.CancelKeyPress -= CancelKeyPress;
            listening = false;
        }

        terminationRegistration?.Dispose();
        terminationRegistration = null;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    public Task WaitForStartAsync(CancellationToken cancellationToken)
    {
        Console.CancelKeyPress += CancelKeyPress;
        listening = true;
        if (!OperatingSystem.IsWindows())
        {
            terminationRegistration = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
            {
                context.Cancel = true;
                applicationLifetime.StopApplication();
            });
        }

        return Task.CompletedTask;
    }

    internal void Interrupt()
    {
        if (!interruptState.IsCommandPending)
        {
            applicationLifetime.StopApplication();
        }
    }

    private void CancelKeyPress(object? sender, ConsoleCancelEventArgs args)
    {
        args.Cancel = true;
        Interrupt();
    }
}