using Microsoft.Extensions.Logging;

namespace OSSMDotNet.ControlService.Activity;

/// <summary>
/// Defines the structured event used for operator-visible activity.
/// </summary>
internal static partial class ControlServiceLog
{
    [LoggerMessage(EventId = 200, EventName = "OperatorActivityOperation", Level = LogLevel.Information, Message = "{Action}.")]
    internal static partial void OperatorActivity(ILogger logger, string action);

}
