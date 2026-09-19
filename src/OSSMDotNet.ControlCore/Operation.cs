using OSSMDotNet.Models;

namespace OSSMDotNet.ControlCore;

internal sealed class Operation(long id, OperationKind kind, MotionSettings desired, long startedAt,
    long generation, long sampleSequence, long connection, bool forEnvelope)
{
    public long Id { get; } = id;
    public OperationKind Kind { get; } = kind;
    public MotionSettings Desired { get; } = desired;
    public long StartedAt { get; } = startedAt;
    public long Generation { get; } = generation;
    public long SampleSequence { get; } = sampleSequence;
    public long Connection { get; } = connection;
    public bool ForEnvelope { get; } = forEnvelope;
    public bool TimedOut { get; set; }
    public long? CompletedAt { get; set; }
    public Task Task { get; set; } = Task.CompletedTask;
}
