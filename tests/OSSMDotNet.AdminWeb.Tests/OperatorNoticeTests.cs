namespace OSSMDotNet.AdminWeb.Tests;

public sealed class OperatorNoticeTests
{
    [Fact]
    public void CopyConfirmationUsesOnlyTheButtonAndExpiresAfterTwoSeconds()
    {
        var time = new FakeTimeProvider();
        var notice = new OperatorNoticeState(time);
        notice.Show("Limits applied.");
        notice.CopyCompleted(true);
        Assert.True(notice.IsCopied);
        Assert.Equal("Copied", notice.CopyLabel);
        Assert.Equal("Limits applied.", notice.Message);
        time.Advance(TimeSpan.FromMilliseconds(1999));
        Assert.Equal("Copied", notice.CopyLabel);
        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal("Copy", notice.CopyLabel);
        Assert.False(notice.IsCopied);
    }

    [Fact]
    public void SuccessMessagesExpireButErrorsRemainUntilTheNextAction()
    {
        var time = new FakeTimeProvider();
        var notice = new OperatorNoticeState(time);
        notice.Show("Limits applied.");
        Assert.Equal("Limits applied.", notice.Message);
        time.Advance(TimeSpan.FromSeconds(3));
        Assert.Null(notice.Message);
        notice.Show("Connection unavailable.", true);
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal("Connection unavailable.", notice.Message);
        Assert.True(notice.IsError);
        notice.Show(null);
        Assert.Null(notice.Message);
        Assert.False(notice.IsError);
    }

    [Fact]
    public void ClipboardFailureDoesNotLeaveASuccessLabel()
    {
        var notice = new OperatorNoticeState(new FakeTimeProvider());
        notice.CopyCompleted(true);
        notice.CopyCompleted(false);
        Assert.Equal("Copy", notice.CopyLabel);
        Assert.False(notice.IsCopied);
        Assert.True(notice.IsError);
        Assert.Equal("Select the link and copy it manually.", notice.Message);
    }

    [Fact]
    public void SuccessfulCopyClearsThePreviousClipboardFailure()
    {
        var notice = new OperatorNoticeState(new FakeTimeProvider());

        notice.CopyCompleted(false);
        notice.CopyCompleted(true);

        Assert.True(notice.IsCopied);
        Assert.Equal("Copied", notice.CopyLabel);
        Assert.False(notice.IsError);
        Assert.Null(notice.Message);
    }
}
