namespace OSSMDotNet.Abstractions;

/// <summary>
/// Indicates that no device observation is available.
/// </summary>
public sealed class NoDeviceDataException : InvalidOperationException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="NoDeviceDataException"/> class with the default message.
    /// </summary>
    public NoDeviceDataException()
        : base("No device observation is available.")
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="NoDeviceDataException"/> class with a specified message.
    /// </summary>
    /// <param name="message">The message describing the unavailable observation.</param>
    public NoDeviceDataException(string? message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="NoDeviceDataException"/> class with a specified message and inner exception.
    /// </summary>
    /// <param name="message">The message describing the unavailable observation.</param>
    /// <param name="innerException">The exception that caused the observation to be unavailable.</param>
    public NoDeviceDataException(string? message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
