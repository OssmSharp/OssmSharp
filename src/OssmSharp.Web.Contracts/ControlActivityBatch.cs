namespace OssmSharp.Web.Contracts;

/// <summary>Represents one size-bounded, ordered page of retained operator activity.</summary>
/// <param name="RetainedFirstSequence">The oldest activity sequence still retained.</param>
/// <param name="RetainedLastSequence">The newest activity sequence still retained.</param>
/// <param name="FirstSequence">The first sequence in this page.</param>
/// <param name="LastSequence">The last sequence in this page.</param>
/// <param name="ContinuationSequence">The cursor for the next page, if more entries remain.</param>
/// <param name="Gap">Whether activity before this page was lost from retention.</param>
/// <param name="Entries">The ordered activity entries in this page.</param>
public sealed record ControlActivityBatch(
    long RetainedFirstSequence,
    long RetainedLastSequence,
    long FirstSequence,
    long LastSequence,
    long? ContinuationSequence,
    bool Gap,
    IReadOnlyList<ControlActivityEntry> Entries);
