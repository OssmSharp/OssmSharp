namespace OSSMDotNet.Models;

/// <summary>Represents the most recently observed device state and connection condition.</summary>
public sealed record DeviceSample(
    long SampleSequence, long ChangeRevision, long? CommandId, DesiredState Desired, bool Enabled, bool Moving,
    double Position, double Velocity, bool Connected, string ProfileId);