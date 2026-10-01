using Microsoft.Extensions.Options;
using OssmSharp.Web.Api.Models;

namespace OssmSharp.Web.Api.Control;

internal sealed class DeviceLeaseManagementService(
    IControlSession session,
    IOptions<ControlServiceOptions> options,
    ILogger<DeviceLeaseManagementService> logger) : BackgroundService
{
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        await session.ShutdownAsync().ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(
            TimeSpan.FromMilliseconds(options.Value.GuestControlCheckMilliseconds));
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await session.ExpireGuestAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or OperationCanceledException)
            {
                ApiLog.RequiredStopRetryFailed(logger, exception);
            }
        }
    }

}
