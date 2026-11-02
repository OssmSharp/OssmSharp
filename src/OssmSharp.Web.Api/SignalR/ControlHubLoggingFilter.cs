using System.Diagnostics;
using Microsoft.AspNetCore.SignalR;

namespace OssmSharp.Web.Api.SignalR;

internal sealed class ControlHubLoggingFilter(ILogger<ControlHub> logger) : IHubFilter
{
    public async ValueTask<object?> InvokeMethodAsync(
        HubInvocationContext invocationContext,
        Func<HubInvocationContext, ValueTask<object?>> next)
    {
        var operation = invocationContext.HubMethodName;
        var level = operation is nameof(ControlHub.GetUserState) or nameof(ControlHub.GetGuestState)
            or nameof(ControlHub.GetSavedSession) or nameof(ControlHub.Renew)
            ? LogLevel.Trace : LogLevel.Information;
        using var scope = logger.BeginScope(new Dictionary<string, object?> { ["ControlOperation"] = operation });
        var started = Stopwatch.GetTimestamp();
        logger.OperationStarted(level, operation);
        object? response = null;
        var completed = false;
        var cancelled = false;
        try
        {
            response = await next(invocationContext).ConfigureAwait(false);
            completed = true;
            return response;
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
            throw;
        }
        finally
        {
            var (result, code, commandsWritten) = response switch
            {
                SettingsResponse settings => (settings.Status.ToString(), settings.ErrorCode, (int?)settings.CommandsWritten),
                ControlResponse control => (control.Accepted ? "Accepted" : "Rejected", control.Code, null),
                GuestRedemptionResponse redemption => (redemption.Accepted ? "Accepted" : "Rejected", redemption.Code, null),
                _ => ("Completed", (string?)null, (int?)null),
            };
            if (!completed)
            {
                result = cancelled ? "Cancelled" : "Faulted";
            }

            var resultLevel = result is "Failed" or "Rejected" or "Faulted" ? LogLevel.Warning
                : result is "Cancelled" or "Superseded" ? LogLevel.Debug : level;
            // ASP.NET logs unhandled exceptions; converted failures retain their cause in ControlSession.
            logger.OperationFinished(resultLevel, operation, result, code, Stopwatch.GetElapsedTime(started).TotalMilliseconds, commandsWritten);
        }
    }
}
