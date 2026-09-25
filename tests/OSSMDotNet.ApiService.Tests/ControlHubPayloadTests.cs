using System.Buffers;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Protocol;

namespace OSSMDotNet.ApiService.Tests;

/// <summary>
/// Checks SignalR frame limits and the rejection behavior for oversized hub payloads.
/// </summary>
public sealed class ControlHubPayloadTests
{
    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public void ExactSignalRFrameBudgetIncludesTheRecordSeparator(int offset)
    {
        var protocol = new JsonHubProtocol();
        var frame = new ArrayBufferWriter<byte>();
        protocol.WriteMessage(new InvocationMessage("guestState", [string.Empty]), frame);
        var payload = new string('a', ControlHubPayload.MaximumBytes - frame.WrittenCount + offset);
        frame.Clear();
        protocol.WriteMessage(new InvocationMessage("guestState", [payload]), frame);

        Assert.Equal(ControlHubPayload.MaximumBytes + offset, frame.WrittenCount);
        Assert.Equal(0x1e, frame.WrittenSpan[^1]);
        Assert.Equal(frame.WrittenCount, ControlHubPayload.InvocationByteCount("guestState", payload));
        if (offset <= 0)
            ControlHubPayload.EnsureFits("guestState", payload);
        else
            Assert.Equal("outbound-payload-too-large", Assert.Throws<HubException>(() =>
                ControlHubPayload.EnsureFits("guestState", payload)).Message);
    }

    [Fact]
    public void FrameSizingDoesNotAllocateASecondPayloadSizedBuffer()
    {
        var small = new string('a', 1_024);
        var large = new string('a', 9_216);
        MeasureFrameSizing(small);
        MeasureFrameSizing(large);

        var smallBytes = MeasureFrameSizing(small);
        var largeBytes = MeasureFrameSizing(large);

        Assert.InRange(largeBytes - smallBytes, 0, (large.Length - small.Length) * 32L * 3 / 2);
    }

    private static long MeasureFrameSizing(string payload)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 32; index++)
            ControlHubPayload.InvocationByteCount("guestState", payload);
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

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
