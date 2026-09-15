namespace OSSMDotNet.Web.AdminService;

public sealed class OperatorNoticeState(TimeProvider time)
{
    private string? _message;
    private long _messageAt;
    private long? _copiedAt;
    public string? Message => IsError || time.GetElapsedTime(_messageAt) < TimeSpan.FromSeconds(3) ? _message : null;
    public bool IsError { get; private set; }
    public bool IsCopied => _copiedAt is { } copied && time.GetElapsedTime(copied) < TimeSpan.FromSeconds(2);
    public string CopyLabel => IsCopied ? "Copied" : "Copy";

    public void Show(string? message, bool isError = false)
    {
        _message = message;
        IsError = isError;
        _messageAt = time.GetTimestamp();
    }

    public void CopyCompleted(bool succeeded)
    {
        _copiedAt = succeeded ? time.GetTimestamp() : null;
        if (!succeeded) Show("Select the link and copy it manually.", true);
    }
}
