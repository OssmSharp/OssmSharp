using Microsoft.AspNetCore.SignalR;

namespace OSSMDotNet.ControlService.Tests;

public sealed class ControlHubPayloadTests
{
    [Fact]
    public void InvocationFrameAtOrBelowTheCurrentBudgetIsAccepted()
    {
        var payload = new string('漢', 2_000);
        var byteCount = ControlHubPayload.InvocationByteCount("guestState", payload);

        ControlHubPayload.EnsureFits("guestState", payload);

        Assert.True(byteCount <= ControlHubPayload.MaximumBytes);
        Assert.True(byteCount > 12_000);
    }

    [Fact]
    public void InvocationFrameAboveTheCurrentBudgetThrowsTheResyncPathHubException()
    {
        var payload = new string('\\', ControlHubPayload.MaximumBytes);
        var byteCount = ControlHubPayload.InvocationByteCount("operatorState", payload);

        var error = Assert.Throws<HubException>(() =>
            ControlHubPayload.EnsureFits("operatorState", payload));

        Assert.Equal("outbound-payload-too-large", error.Message);
        Assert.True(byteCount > ControlHubPayload.MaximumBytes);
    }

    [Fact]
    public void EscapedQuotePayloadAlsoUsesTheCurrentHubFrameRejectionCode()
    {
        var payload = new string('"', ControlHubPayload.MaximumBytes);

        var error = Assert.Throws<HubException>(() =>
            ControlHubPayload.EnsureFits("operatorLog", payload));

        Assert.Equal("outbound-payload-too-large", error.Message);
        Assert.True(ControlHubPayload.InvocationByteCount("operatorLog", payload) > ControlHubPayload.MaximumBytes);
    }
}
