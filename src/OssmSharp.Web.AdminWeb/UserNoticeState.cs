namespace OssmSharp.Web.AdminWeb;

/// <summary>
/// Tracks the user notice and temporary copy confirmation state.
/// </summary>
/// <param name="time">The clock used to expire temporary notices.</param>
public sealed class UserNoticeState(TimeProvider time)
{
    private string? _message;
    private long _messageAt;
    private long? _copiedAt;
    /// <summary>
    /// Gets the active notice; non-error notices expire after three seconds.
    /// </summary>
    public string? Message => IsError || time.GetElapsedTime(_messageAt) < TimeSpan.FromSeconds(3) ? _message : null;
    /// <summary>
    /// Gets whether the notice remains visible until replaced or cleared.
    /// </summary>
    public bool IsError { get; private set; }
    /// <summary>
    /// Gets whether the copy confirmation is still visible within its two-second window.
    /// </summary>
    public bool IsCopied => _copiedAt is { } copied && time.GetElapsedTime(copied) < TimeSpan.FromSeconds(2);
    /// <summary>
    /// Gets the label for the invitation copy action during its confirmation window.
    /// </summary>
    public string CopyLabel => IsCopied ? "Copied" : "Copy";

    /// <summary>
    /// Shows an user notice, optionally retaining it as an error.
    /// </summary>
    /// <param name="message">The notice to show, or null to clear it.</param>
    /// <param name="isError">Whether the notice stays visible until replaced.</param>
    public void Show(string? message, bool isError = false)
    {
        _message = message;
        IsError = isError;
        _messageAt = time.GetTimestamp();
    }

    /// <summary>
    /// Records copy success or shows a persistent manual-copy instruction on failure.
    /// </summary>
    /// <param name="succeeded">Whether the invitation link reached the clipboard.</param>
    public void CopyCompleted(bool succeeded)
    {
        if (succeeded)
        {
            _copiedAt = time.GetTimestamp();
            if (IsError && _message == "Select the link and copy it manually.") Show(null);
            return;
        }

        _copiedAt = null;
        Show("Select the link and copy it manually.", true);
    }
}
