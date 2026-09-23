namespace OSSMDotNet.Api.Hosting;

internal static partial class ApiLog
{
    [LoggerMessage(EventId = 201, EventName = "OperatorActivitySafety", Level = LogLevel.Warning, Message = "{Status}.")]
    internal static partial void DeviceSafetyState(ILogger logger, string status);

    [LoggerMessage(EventId = 202, EventName = "OperatorActivityOwnership", Level = LogLevel.Critical,
        Message = "Another control authority already controls the device. Stop that service before starting this one.")]
    internal static partial void AuthorityAlreadyOwned(ILogger logger);

    [LoggerMessage(EventId = 203, EventName = "ControlServiceDeliveryFailed", Level = LogLevel.Warning,
        Message = "A control-service client delivery failed; resynchronization was requested.")]
    internal static partial void ClientDeliveryFailed(ILogger logger);
}
