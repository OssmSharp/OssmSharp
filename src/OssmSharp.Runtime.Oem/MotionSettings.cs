namespace OssmSharp.Runtime.Oem;

/// <summary>
/// Carries complete firmware settings, validating basic ranges without interpreting patterns.
/// </summary>
public sealed record MotionSettings
{
    /// <summary>
    /// Initializes a complete set of control arguments. Construction sends nothing.
    /// </summary>
    /// <param name="pattern">A defined firmware stroke pattern.</param>
    /// <param name="speed">The speed argument from zero through 100 percent.</param>
    /// <param name="stroke">The stroke argument from zero through 100 percent.</param>
    /// <param name="depth">The depth argument from zero through 100 percent.</param>
    /// <param name="sensation">The sensation argument from zero through 100 percent.</param>
    /// <exception cref="ArgumentOutOfRangeException">The pattern is undefined, or a percentage is outside its basic range.</exception>
    public MotionSettings(StrokePattern pattern, int speed, int stroke, int depth, int sensation)
    {
        if (!IsValidPattern(pattern))
        {
            throw new ArgumentOutOfRangeException(nameof(pattern), pattern, "Pattern must be a defined stroke pattern.");
        }

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
    /// Gets the firmware stroke pattern.
    /// </summary>
    public StrokePattern Pattern { get; }

    /// <summary>
    /// Gets the speed argument as a percentage.
    /// </summary>
    public int Speed { get; }

    /// <summary>
    /// Gets the stroke argument as a percentage.
    /// </summary>
    public int Stroke { get; }

    /// <summary>
    /// Gets the depth argument as a percentage.
    /// </summary>
    public int Depth { get; }

    /// <summary>
    /// Gets the sensation argument as a percentage.
    /// </summary>
    public int Sensation { get; }

    internal DeviceCommand[] ToCommands() =>
    [
        new(DeviceControl.Pattern, (int)Pattern),
        new(DeviceControl.Stroke, Stroke),
        new(DeviceControl.Depth, Depth),
        new(DeviceControl.Sensation, Sensation),
        new(DeviceControl.Speed, Speed),
    ];

    internal static bool IsValidPattern(StrokePattern value) => Enum.IsDefined(value);

    internal static bool IsValidPercentage(int value) => value is >= 0 and <= 100;

    private static void ValidatePercentage(int value, string parameterName)
    {
        if (!IsValidPercentage(value))
        {
            throw new ArgumentOutOfRangeException(parameterName, value, "Percentage must be between zero and 100.");
        }
    }
}
