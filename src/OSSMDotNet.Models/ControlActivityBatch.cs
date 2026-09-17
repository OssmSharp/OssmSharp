namespace OSSMDotNet.Models;

/// <summary>Represents one size-bounded, ordered page of retained operator activity.</summary>
public sealed record ControlActivityBatch(
    long RetainedFirstSequence,
    long RetainedLastSequence,
    long FirstSequence,
    long LastSequence,
    long? ContinuationSequence,
    bool Gap,
    IReadOnlyList<ControlActivityEntry> Entries);
