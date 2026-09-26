using System.Diagnostics.CodeAnalysis;

namespace OssmSharp.Device.Simulator.Models;

/// <summary>
/// Represents one reported simulator sample and its accepted motion settings.
/// </summary>
/// <param name="SampleSequence">The reported sample sequence.</param>
/// <param name="ChangeRevision">The revision of simulator state changes.</param>
/// <param name="CommandId">The accepted device command identifier, if any.</param>
/// <param name="Desired">The accepted motion settings.</param>
/// <param name="Enabled">Whether the simulator reports enabled motion.</param>
/// <param name="Moving">Whether the simulator reports movement.</param>
/// <param name="Position">The reported position in millimetres.</param>
/// <param name="Velocity">The reported velocity in millimetres per second.</param>
/// <param name="Connected">Whether the simulated device is connected.</param>
/// <param name="ProfileId">The simulator profile identifier.</param>
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
    /// <summary>
    /// Rejects invalid telemetry, motion settings, or a mismatched simulator profile.
    /// </summary>
    /// <exception cref="InvalidDataException">The sample violates the simulator protocol.</exception>
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
