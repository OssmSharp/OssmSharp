namespace OssmSharp.Runtime.Oem;

/// <summary>
/// Assigns one value to one firmware control without interpreting pattern behavior.
/// </summary>
public readonly record struct DeviceCommand
{
    /// <summary>
    /// Initializes a validated control assignment.
    /// </summary>
    /// <param name="control">The firmware control to assign.</param>
    /// <param name="value">An integer pattern identifier or a percentage from zero through 100.</param>
/// <exception cref="ArgumentOutOfRangeException">The control or value is outside its basic range.</exception>
    public DeviceCommand(DeviceControl control, double value)
    {
        if (!Enum.IsDefined(control))
        {
            throw new ArgumentOutOfRangeException(nameof(control), control, "Unknown firmware control.");
        }

        var maximum = control == DeviceControl.Pattern ? int.MaxValue : 100;
        if (!double.IsFinite(value) || value < 0 || value > maximum
            || (control == DeviceControl.Pattern && value != Math.Truncate(value)))
        {
            throw new ArgumentOutOfRangeException(nameof(value), value,
                control == DeviceControl.Pattern
                    ? "Pattern must be a nonnegative 32-bit integer."
                    : "Percentage must be finite and between zero and 100.");
        }

        Control = control;
        Value = value;
    }

    /// <summary>
    /// Gets the firmware control being assigned.
    /// </summary>
    public DeviceControl Control { get; }

    /// <summary>
    /// Gets the pattern identifier or percentage argument.
    /// </summary>
    public double Value { get; }
}
