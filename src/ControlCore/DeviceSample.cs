namespace OSSMDotNet.DeviceControl;

public sealed record DeviceSample(
    long SampleSequence,
    long ChangeRevision,
    long? CommandId,
    DesiredState Desired,
    bool Enabled,
    bool Moving,
    double Position,
    double Velocity,
    bool Connected,
    string ProfileId);
