namespace OSSMDotNet.ControlService;

/// <summary>Writes one already-authorized callback to a hub connection.</summary>
public interface IControlHubPushSink
{
    /// <summary>Sends a callback without retaining application state or credentials.</summary>
    Task SendAsync(string connectionId, string method, object payload, CancellationToken cancellationToken);
}
