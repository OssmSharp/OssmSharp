namespace OssmSharp.Runtime.Oem;

/// <summary>
/// Contains the last observed firmware controls, with no expiry or inference about physical motion or safety.
/// </summary>
public sealed record DeviceState
{
    /// <summary>
    /// Initializes an observation of the device's control arguments.
    /// </summary>
    /// <param name="settings">The observed control arguments, not a requested motion or physical position.</param>
    public DeviceState(MotionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Settings = settings;
    }

    /// <summary>
    /// Gets the observed firmware settings.
    /// </summary>
    public MotionSettings Settings { get; }
}
