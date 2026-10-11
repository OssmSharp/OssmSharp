namespace OssmSharp.Web.Contracts;

/// <summary>
/// Defines stable codes returned by control-service operations.
/// </summary>
public static class ControlResponseCodes
{
    /// <summary>
    /// The shared preview dimensions were updated without changing motion settings.
    /// </summary>
    public const string PreviewUpdated = "preview-updated";

    /// <summary>
    /// Preview dimensions were missing, non-finite, or outside their allowed centimetre ranges.
    /// </summary>
    public const string InvalidPreviewDimensions = "invalid-preview-dimensions";

    /// <summary>
    /// The request correlation was missing, too long, or contained a control character.
    /// </summary>
    public const string InvalidCorrelation = "invalid-correlation";

    /// <summary>
    /// A guest already owns the current session.
    /// </summary>
    public const string GuestAlreadyJoined = "guest-already-joined";

    /// <summary>
    /// The invitation capability was invalid.
    /// </summary>
    public const string InvalidInvitation = "invalid-invitation";

    /// <summary>
    /// The guest joined the current session.
    /// </summary>
    public const string Joined = "joined";

    /// <summary>
    /// The caller is not the guest that joined the current session.
    /// </summary>
    public const string GuestNotJoined = "guest-not-joined";

    /// <summary>
    /// The current guest was allowed to submit settings.
    /// </summary>
    public const string ControlAllowed = "control-allowed";

    /// <summary>
    /// The current guest is not allowed to submit settings.
    /// </summary>
    public const string ControlNotAllowed = "control-not-allowed";

    /// <summary>
    /// The current guest permission was renewed.
    /// </summary>
    public const string ControlRenewed = "control-renewed";

    /// <summary>
    /// The submitted motion settings were invalid.
    /// </summary>
    public const string InvalidSettings = "invalid-settings";

    /// <summary>
    /// The current guest permission expired.
    /// </summary>
    public const string ControlExpired = "control-expired";

    /// <summary>
    /// The runtime failed after writing one or more device commands.
    /// </summary>
    public const string DeviceWriteFailed = "device-write-failed";

    /// <summary>
    /// The runtime device was not ready for the requested operation.
    /// </summary>
    public const string DeviceNotReady = "device-not-ready";

    /// <summary>
    /// The runtime device operation failed because of an I/O error.
    /// </summary>
    public const string DeviceIoFailed = "device-io-failed";

    /// <summary>
    /// The device no longer grants this connection control.
    /// </summary>
    public const string DeviceControlLost = "device-control-lost";

    /// <summary>
    /// The runtime device connected successfully.
    /// </summary>
    public const string Connected = "connected";

    /// <summary>
    /// The requested operation was cancelled.
    /// </summary>
    public const string Cancelled = "cancelled";

    /// <summary>
    /// The runtime accepted the Stop request.
    /// </summary>
    public const string Stopped = "stopped";

    /// <summary>
    /// The one-shot position reset completed and stopped without resuming settings.
    /// </summary>
    public const string PositionReset = "position-reset";

    /// <summary>
    /// The runtime device was not connected for the requested operation.
    /// </summary>
    public const string DeviceNotConnected = "device-not-connected";
}
