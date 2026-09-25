using Microsoft.Extensions.Logging;

namespace OSSMDotNet.ControlService.Activity;

/// <summary>
/// Defines the structured event used for operator-visible activity.
/// </summary>
internal static partial class ControlServiceLog
{
    [LoggerMessage(EventId = 200, EventName = "OperatorActivityOperation", Level = LogLevel.Information, Message = "{Action}.")]
    internal static partial void OperatorActivity(ILogger logger, string action);

    [LoggerMessage(EventId = 201, EventName = "OperatorAuthorityRejected", Level = LogLevel.Debug, Message = "Operator {Operation} rejected: {Reason}.")]
    internal static partial void OperatorRejected(this ILogger logger, string operation, string reason);

    [LoggerMessage(EventId = 202, EventName = "GuestAuthorityAdmitted", Level = LogLevel.Information, Message = "Guest admitted pending operator approval.")]
    internal static partial void GuestAdmitted(this ILogger logger);

    [LoggerMessage(EventId = 203, EventName = "ControllerSafetyState", Level = LogLevel.Warning, Message = "Controller entered {Mode}: {Reason}; motion authority is revoked.")]
    internal static partial void SafetyState(this ILogger logger, string mode, string reason);

    [LoggerMessage(EventId = 204, EventName = "GuestAuthorityRevoked", Level = LogLevel.Information, Message = "Guest authority revoked: {Reason}.")]
    internal static partial void GuestRevoked(this ILogger logger, string reason);

    [LoggerMessage(EventId = 205, EventName = "GuestStopAccepted", Level = LogLevel.Information, Message = "Guest Stop accepted; motion authority revoked.")]
    internal static partial void GuestStopAccepted(this ILogger logger);

    [LoggerMessage(EventId = 206, EventName = "GuestAdmissionRejected", Level = LogLevel.Debug, Message = "Guest admission rejected: {Reason}.")]
    internal static partial void GuestAdmissionRejected(this ILogger logger, string reason);

}
