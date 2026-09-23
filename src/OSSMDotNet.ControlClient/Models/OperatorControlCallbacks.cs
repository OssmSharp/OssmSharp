using OSSMDotNet.Models;

namespace OSSMDotNet.ControlClient.Models;

/// <summary>
/// Defines the single ordered callback set for one operator control connection.
/// </summary>
/// <param name="StateReceived">Handles an operator state and invitation update.</param>
/// <param name="ActivityReceived">Handles a bounded page of operator activity.</param>
/// <param name="ResyncRequired">Handles a request to reload state after a missed update.</param>
public sealed record OperatorControlCallbacks(
    Func<OperatorLiveControlStatus, Task> StateReceived,
    Func<ControlActivityBatch, Task> ActivityReceived,
    Func<ControlResyncRequired, Task> ResyncRequired);
