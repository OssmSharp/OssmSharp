namespace OssmSharp.Web.Contracts;

/// <summary>
/// Identifies the caller-visible result of a settings submission.
/// </summary>
public enum SettingsResponseStatus
{
    /// <summary>The runtime completed all settings writes.</summary>
    Completed,

    /// <summary>The runtime superseded the submission with a newer submission.</summary>
    Superseded,

    /// <summary>The runtime cancelled the submission.</summary>
    Cancelled,

    /// <summary>The API rejected the request before invoking the runtime.</summary>
    Rejected,

    /// <summary>The runtime failed while processing the submission.</summary>
    Failed
}
