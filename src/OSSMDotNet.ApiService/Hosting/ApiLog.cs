namespace OSSMDotNet.ApiService.Hosting;

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

    [LoggerMessage(EventId = 204, EventName = "ControlHubInvocationRejected", Level = LogLevel.Debug,
        Message = "Control hub {Operation} rejected: {Code}.")]
    internal static partial void HubInvocationRejected(ILogger logger, string operation, string code);

    [LoggerMessage(EventId = 205, EventName = "ControlHubLifecycleAccepted", Level = LogLevel.Information,
        Message = "Control hub {Operation} accepted.")]
    internal static partial void HubLifecycleAccepted(ILogger logger, string operation);
}
