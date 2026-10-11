namespace OssmSharp.Runtime.Oem;

/// <summary>
/// Provides the firmware rejection code and state retained as the cause of a device I/O failure.
/// </summary>
/// <param name="code">The firmware rejection code, or <c>unknown</c> when absent.</param>
/// <param name="deviceState">The firmware state after the rejected request, when supplied.</param>
/// <param name="message">The firmware's explanation, when supplied.</param>
public sealed class DeviceRequestException(string code, string? deviceState, string? message = null)
    : IOException($"The device rejected the request: {code}{(message is null ? string.Empty : $" ({message})")}.")
{
    /// <summary>
    /// Gets the firmware rejection code.
    /// </summary>
    public string Code { get; } = code;

    /// <summary>
    /// Gets the firmware state after the rejected request, or null when unavailable.
    /// </summary>
    public string? DeviceState { get; } = deviceState;
}
