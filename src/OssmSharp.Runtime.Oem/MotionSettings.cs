namespace OssmSharp.Runtime.Oem;

/// <summary>
/// Carries complete firmware settings, validating basic ranges without interpreting patterns.
/// </summary>
public sealed record MotionSettings
{
    /// <summary>
    /// Initializes a complete set of control arguments. Construction sends nothing.
    /// </summary>
    /// <param name="pattern">A nonnegative firmware pattern identifier.</param>
    /// <param name="speed">The speed argument from zero through 100 percent.</param>
    /// <param name="stroke">The stroke argument from zero through 100 percent.</param>
    /// <param name="depth">The depth argument from zero through 100 percent.</param>
    /// <param name="sensation">The sensation argument from zero through 100 percent.</param>
    /// <exception cref="ArgumentOutOfRangeException">An argument is outside its basic range or is not finite.</exception>
    public MotionSettings(int pattern, double speed, double stroke, double depth, double sensation)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pattern);
        ValidatePercentage(speed, nameof(speed));
        ValidatePercentage(stroke, nameof(stroke));
        ValidatePercentage(depth, nameof(depth));
        ValidatePercentage(sensation, nameof(sensation));
        Pattern = pattern;
        Speed = speed;
        Stroke = stroke;
        Depth = depth;
        Sensation = sensation;
    }

    /// <summary>
    /// Gets the firmware pattern identifier.
    /// </summary>
    public int Pattern { get; }

    /// <summary>
    /// Gets the speed argument as a percentage.
    /// </summary>
    public double Speed { get; }

    /// <summary>
    /// Gets the stroke argument as a percentage.
    /// </summary>
    public double Stroke { get; }

    /// <summary>
    /// Gets the depth argument as a percentage.
    /// </summary>
    public double Depth { get; }

    /// <summary>
    /// Gets the sensation argument as a percentage.
    /// </summary>
    public double Sensation { get; }

    internal DeviceCommand[] ToCommands() =>
    [
        new(DeviceControl.Pattern, Pattern),
        new(DeviceControl.Stroke, Stroke),
        new(DeviceControl.Depth, Depth),
        new(DeviceControl.Sensation, Sensation),
        new(DeviceControl.Speed, Speed),
    ];

    private static void ValidatePercentage(double value, string parameterName)
    {
        if (!double.IsFinite(value) || value < 0 || value > 100)
        {
            throw new ArgumentOutOfRangeException(parameterName, value, "Percentage must be finite and between zero and 100.");
        }
    }
}
