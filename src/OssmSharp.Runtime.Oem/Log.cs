using Microsoft.Extensions.Logging;

namespace OssmSharp.Runtime.Oem;

internal static partial class Log
{
    [LoggerMessage(2, LogLevel.Error, "Device operation {Operation} failed; commands remain disarmed.")]
    public static partial void OperationFailed(this ILogger logger, string operation, Exception exception);

    [LoggerMessage(3, LogLevel.Warning, "A device state subscriber failed.")]
    public static partial void SubscriberFailed(this ILogger logger, Exception exception);

    [LoggerMessage(4, LogLevel.Debug, "Discarded an invalid motion observation.")]
    public static partial void ObservationDiscarded(this ILogger logger, Exception exception);

    [LoggerMessage(5, LogLevel.Debug, "Discarded a malformed device response.")]
    public static partial void ResponseDiscarded(this ILogger logger, Exception exception);

    [LoggerMessage(6, LogLevel.Debug, "Resending device request {Operation} after its response was not received.")]
    public static partial void RequestResent(this ILogger logger, string operation);

    [LoggerMessage(7, LogLevel.Debug, "Control lease acquired with a lifetime of {LeaseLifetimeMilliseconds} ms.")]
    public static partial void LeaseAcquired(this ILogger logger, double leaseLifetimeMilliseconds);

    [LoggerMessage(8, LogLevel.Debug, "Renewing control lease with {RemainingLifetimeMilliseconds} ms remaining.")]
    public static partial void LeaseRenewalStarted(this ILogger logger, double remainingLifetimeMilliseconds);

    [LoggerMessage(9, LogLevel.Debug, "Control lease renewed in {RenewalDurationMilliseconds} ms with a lifetime of {LeaseLifetimeMilliseconds} ms.")]
    public static partial void LeaseRenewed(this ILogger logger, double renewalDurationMilliseconds, double leaseLifetimeMilliseconds);

    [LoggerMessage(10, LogLevel.Debug, "Control lease renewal did not complete within its {RenewalDeadlineMilliseconds} ms deadline.")]
    public static partial void LeaseRenewalExpired(this ILogger logger, double renewalDeadlineMilliseconds);
}
