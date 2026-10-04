namespace OssmSharp.Web.Contracts;

/// <summary>
/// Identifies the caller-visible result of a settings request.
/// </summary>
public enum SettingsResponseStatus
{
    /// <summary>
    /// The runtime completed all settings writes.
    /// </summary>
    Completed,

    /// <summary>
    /// The runtime superseded the request with a newer request.
    /// </summary>
    Superseded,

    /// <summary>
    /// The runtime cancelled the request.
    /// </summary>
    Cancelled,

    /// <summary>
    /// The API rejected the request before invoking the runtime.
    /// </summary>
    Rejected,

    /// <summary>
    /// The runtime failed while processing the request.
    /// </summary>
    Failed,
}
