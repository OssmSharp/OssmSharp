namespace OssmSharp.Web.Application.Models;

/// <summary>
/// Tracks a completed stop write until feedback confirms it for the same connection and generation.
/// </summary>
/// <param name="CompletedAt">The monotonic time when the stop write completed.</param>
/// <param name="SampleSequence">The device sample sequence preceding the write.</param>
/// <param name="Connection">The device connection that owns the write.</param>
/// <param name="Generation">The control generation that owns the write.</param>
/// <param name="ForEnvelope">Whether the stop write belongs to the motion envelope.</param>
internal sealed record StopFeedback(long CompletedAt, long SampleSequence, long Connection, long Generation, bool ForEnvelope);
