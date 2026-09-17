namespace OSSMDotNet.AdminWeb;

/// <summary>Tracks the operator notice and temporary copy confirmation state.</summary>
public sealed class OperatorNoticeState(TimeProvider time)
{
    private string? _message;
    private long _messageAt;
    private long? _copiedAt;
    public string? Message => IsError || time.GetElapsedTime(_messageAt) < TimeSpan.FromSeconds(3) ? _message : null;
    public bool IsError { get; private set; }
    public bool IsCopied => _copiedAt is { } copied && time.GetElapsedTime(copied) < TimeSpan.FromSeconds(2);
    public string CopyLabel => IsCopied ? "Copied" : "Copy";

    /// <summary>Shows an operator notice, optionally retaining it as an error.</summary>
    public void Show(string? message, bool isError = false)
    {
        _message = message;
        IsError = isError;
        _messageAt = time.GetTimestamp();
    }

    /// <summary>Records whether copying the invitation link succeeded.</summary>
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
