using System.ComponentModel.DataAnnotations;

namespace OSSMDotNet.Abstractions.Models;

/// <summary>
/// Represents the current settings of a motion device.
/// </summary>
/// <param name="State">The current device state.</param>
/// <param name="Speed">The effective speed percentage after applying the physical speed-knob limit.</param>
/// <param name="Stroke">The stroke length as a percentage of the calibrated travel.</param>
/// <param name="Sensation">The mode-dependent sensation setting from 0 to 100.</param>
/// <param name="Depth">The maximum penetration depth as a percentage of the calibrated travel.</param>
/// <param name="Buffer">The streaming latency-compensation buffer from 0 to 100, representing up to twice the value in milliseconds.</param>
/// <param name="Pattern">The selected stroke-engine motion pattern.</param>
/// <param name="Position">The current stepper position in millimetres, calculated from the firmware's internal step count.</param>
public sealed record MotionSettings(
    [property: EnumDataType(typeof(MotionDeviceState))] MotionDeviceState State,
    [property: Range(0, 100)] int Speed,
    [property: Range(0, 100)] int Stroke,
    [property: Range(0, 100)] int Sensation,
    [property: Range(0, 100)] int Depth,
    [property: Range(0, 100)] int Buffer,
    [property: EnumDataType(typeof(MotionPattern))] MotionPattern Pattern,
    [property: Range(double.MinValue, double.MaxValue)] double Position) : IValidatableObject
{
    /// <inheritdoc />
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!double.IsFinite(Position))
        {
            yield return new ValidationResult(
                "Position must be finite.",
                [nameof(Position)]);
        }

        if (Speed > 0 && Stroke == 0)
        {
            yield return new ValidationResult(
                "Nonzero speed requires a positive stroke.",
                [nameof(Speed), nameof(Stroke)]);
        }

        if (Stroke > Depth)
        {
            yield return new ValidationResult(
                "Stroke must not exceed depth.",
                [nameof(Stroke), nameof(Depth)]);
        }
    }
}
