using OSSMDotNet.Models;

namespace OSSMDotNet.ControlClient;

/// <summary>Defines the single ordered callback set for one guest control connection.</summary>
public sealed record GuestControlCallbacks(
    Func<GuestStatus, Task> StateReceived,
    Func<ControlResyncRequired, Task> ResyncRequired);
