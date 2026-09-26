namespace OssmSharp.Web.Api.Models;

/// <summary>
/// Reports whether a live projection entered the bounded delivery queue.
/// </summary>
internal enum QueueState
{
    /// <summary>
    /// Indicates an unchanged or ineligible projection was not queued.
    /// </summary>
    Ignored,
    /// <summary>
    /// Indicates a projection was accepted for delivery.
    /// </summary>
    Queued,
    /// <summary>
    /// Indicates the bounded delivery queue cannot accept this projection.
    /// </summary>
    Saturated
}
