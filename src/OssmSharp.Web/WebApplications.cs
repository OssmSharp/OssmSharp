using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace OssmSharp.Web;

/// <summary>
/// Starts the three applications and unwinds their lifetimes in reverse order.
/// </summary>
public sealed class WebApplications(
    IWebApplicationFactory factory,
    IHostApplicationLifetime lifetime,
    IOptions<HostOptions> options) : BackgroundService
{
    /// <inheritdoc />
    /// <remarks>
    /// Caller cancellation does not bypass reverse-order cleanup. Each application
    /// receives a fresh shutdown timeout from <see cref="HostOptions.ShutdownTimeout"/>.
    /// Failures remain on <see cref="BackgroundService.ExecuteTask"/> for the outer
    /// host's logging and process exit code.
    /// </remarks>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        // Finish bounded application cleanup even if the caller's waiting token expires.
        // The outer host logs ExecuteTask failures; Program uses them for the exit code.
        if (ExecuteTask is { } execution)
            await execution.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    /// <inheritdoc />
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => RunAsync(0, stoppingToken);

    private async Task RunAsync(int index, CancellationToken stoppingToken)
    {
        if (index == 3)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
            }
            return;
        }

        var application = factory.Create(index);
        List<Exception> failures = [];
        try
        {
            using var stopped = application.Services.GetRequiredService<IHostApplicationLifetime>()
                .ApplicationStopping.Register(lifetime.StopApplication);
            await application.StartAsync(stoppingToken).ConfigureAwait(false);
            await RunAsync(index + 1, stoppingToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        // Shutdown has a fresh timeout after cancellation; retain every failure for the outer host's log.
        try
        {
            using var shutdown = new CancellationTokenSource(options.Value.ShutdownTimeout);
            await application.StopAsync(shutdown.Token).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
        try
        {
            application.Dispose();
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        if (failures.Count == 1)
            ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures.Count > 1)
            throw new AggregateException("Web application execution or cleanup failed.", failures);
    }
}
