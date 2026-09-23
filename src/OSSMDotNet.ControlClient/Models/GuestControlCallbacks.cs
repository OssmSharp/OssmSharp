using OSSMDotNet.Models;

namespace OSSMDotNet.ControlClient.Models;

/// <summary>
/// Defines the single ordered callback set for one guest control connection.
/// </summary>
/// <param name="StateReceived">Handles a guest-safe status update.</param>
/// <param name="ResyncRequired">Handles a request to reload status after a missed update.</param>
public sealed record GuestControlCallbacks(
    Func<GuestStatus, Task> StateReceived,
    Func<ControlResyncRequired, Task> ResyncRequired);
