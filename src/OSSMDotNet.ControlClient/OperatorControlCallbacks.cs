using OSSMDotNet.Models;

namespace OSSMDotNet.ControlClient;

/// <summary>Defines the single ordered callback set for one operator control connection.</summary>
public sealed record OperatorControlCallbacks(
    Func<OperatorLiveControlStatus, Task> StateReceived,
    Func<ControlActivityBatch, Task> ActivityReceived,
    Func<ControlResyncRequired, Task> ResyncRequired);
