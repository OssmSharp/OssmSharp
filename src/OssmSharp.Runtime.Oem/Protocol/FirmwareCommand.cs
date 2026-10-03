namespace OssmSharp.Runtime.Oem.Protocol;

/// <summary>
/// Assigns one value to one firmware control without interpreting pattern behavior.
/// </summary>
/// <remarks>
/// This is a raw transport assignment, not requested motion settings. <see cref="FirmwareSetting.Speed"/>
/// assigns raw velocity, not speed; no conversion is performed here.
/// Use <see cref="MotionSettings"/> to request speed (as an integer percentage). Percentages are not physical speed
/// or strokes per minute.
/// </remarks>
internal readonly record struct FirmwareCommand
{
    /// <summary>
    /// Initializes a validated control assignment.
    /// </summary>
    /// <param name="control">The firmware control to assign.</param>
    /// <param name="value">A defined <see cref="StrokePattern"/> value or a whole percentage from zero through 100, using 50 for 50%; for Speed, this is raw firmware velocity independent of stroke.</param>
    /// <exception cref="ArgumentOutOfRangeException">The control or value is outside its basic range.</exception>
    public FirmwareCommand(FirmwareSetting control, int value)
    {
        if (!Enum.IsDefined(control))
        {
            throw new ArgumentOutOfRangeException(nameof(control), control, "Unknown firmware control.");
        }

        var maximum = control == FirmwareSetting.Pattern ? int.MaxValue : 100;
        if (value < 0 || value > maximum
            || (control == FirmwareSetting.Pattern && !MotionSettings.IsValidPattern((StrokePattern)value)))
        {
            throw new ArgumentOutOfRangeException(nameof(value), value,
                control == FirmwareSetting.Pattern
                    ? "Pattern must be a defined stroke pattern."
                    : "Percentage must be between zero and 100.");
        }

        Setting = control;
        Value = value;
    }

    /// <summary>
    /// Gets the firmware setting being assigned.
    /// </summary>
    public FirmwareSetting Setting { get; }

    /// <summary>
    /// Gets the pattern identifier or raw whole-number percentage; for Speed, the firmware velocity from zero through 100.
    /// </summary>
    /// <remarks>No speed conversion or rounding is performed on this transport value.</remarks>
    public int Value { get; }
}
