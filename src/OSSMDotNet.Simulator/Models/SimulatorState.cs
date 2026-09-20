using System.Diagnostics.CodeAnalysis;

namespace OSSMDotNet.Simulator.Models;

[SuppressMessage("Performance", "CA1812", Justification = "Instances are created by System.Text.Json.")]
internal sealed record SimulatorState(
    long SampleSequence,
    long ChangeRevision,
    long? CommandId,
    SimulatorDesired Desired,
    bool Enabled,
    bool Moving,
    double Position,
    double Velocity,
    bool Connected,
    string ProfileId)
{
    internal void Validate()
    {
        if (SampleSequence <= 0 ||
            ChangeRevision < 0 ||
            CommandId is <= 0 ||
            Desired is not { } desired ||
            desired.Pattern != (int)MotionPattern.SimpleStroke ||
            desired.Speed is < 0 or > 100 ||
            desired.Stroke is < 0 or > 100 ||
            desired.Depth is < 0 or > 100 ||
            desired.Sensation is < 0 or > 100 ||
            desired.Stroke > desired.Depth ||
            (desired.Speed != 0 && desired.Stroke == 0) ||
            !double.IsFinite(Position) ||
            !double.IsFinite(Velocity))
        {
            throw new InvalidDataException("The simulator returned an invalid state.");
        }

        if (!string.Equals(ProfileId, "ossm-rs-sim-v1", StringComparison.Ordinal))
        {
            throw new InvalidDataException("The simulator returned an unsupported profile.");
        }
    }
}
