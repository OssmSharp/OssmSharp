namespace OSSMDotNet.ControlCore;

internal sealed record StopFeedback(long CompletedAt, long SampleSequence, long Connection, long Generation, bool ForEnvelope);
