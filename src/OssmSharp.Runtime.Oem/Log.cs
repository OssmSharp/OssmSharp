using Microsoft.Extensions.Logging;
using OssmSharp.Runtime.Oem.Protocol;

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

    [LoggerMessage(11, LogLevel.Debug, "Device operation {Operation} started.")]
    public static partial void OperationStarted(this ILogger logger, string operation);

    [LoggerMessage(12, LogLevel.Debug, "Device operation {Operation} completed.")]
    public static partial void OperationCompleted(this ILogger logger, string operation);

    [LoggerMessage(13, LogLevel.Information, "Device connection setup was cancelled; awaiting cleanup.")]
    public static partial void ConnectionCancelled(this ILogger logger);

    [LoggerMessage(14, LogLevel.Debug, "Bluetooth connection setup: {Stage}.")]
    public static partial void ConnectionSetupStage(this ILogger logger, string stage);

    [LoggerMessage(15, LogLevel.Debug, "Device request {RequestId} for {Operation}: GATT transfer {Attempt} completed; awaiting terminal response.")]
    public static partial void RequestTransferred(this ILogger logger, uint requestId, string operation, int attempt);

    [LoggerMessage(16, LogLevel.Trace, "Device request {RequestId} received response stage {Stage} with success {Success}.")]
    public static partial void RequestResponseReceived(this ILogger logger, uint requestId, string stage, bool success);

    [LoggerMessage(17, LogLevel.Information, "Device API {Operation} called.")]
    public static partial void ApiCalled(this ILogger logger, string operation);

    [LoggerMessage(18, LogLevel.Information, "Device connection state: {ConnectionState}.")]
    public static partial void ConnectionStateChanged(this ILogger logger, string connectionState);

    [LoggerMessage(19, LogLevel.Information, "Device Set finished with status {Status}; {CommandsWritten} commands written.")]
    public static partial void SettingsFinished(this ILogger logger, SetStatus status, int commandsWritten);

    [LoggerMessage(20, LogLevel.Information, "Device Stop command completed; commands remain disarmed.")]
    public static partial void StopCompleted(this ILogger logger);

    [LoggerMessage(21, LogLevel.Debug, "Writing device setting {Setting} with value {Value}.")]
    public static partial void CommandStarted(this ILogger logger, FirmwareSetting setting, int value);

    [LoggerMessage(22, LogLevel.Debug, "Device setting {Setting} write completed.")]
    public static partial void CommandCompleted(this ILogger logger, FirmwareSetting setting);

    [LoggerMessage(23, LogLevel.Debug, "Sending device request {RequestId} for {Operation} at {Path}, attempt {Attempt}.")]
    public static partial void RequestSending(this ILogger logger, uint requestId, string operation, string? path, int attempt);

    [LoggerMessage(24, LogLevel.Debug, "Device request {RequestId} received its terminal response with success {Success}.")]
    public static partial void RequestCompleted(this ILogger logger, uint requestId, bool success);
}
