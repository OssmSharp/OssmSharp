using System.Buffers.Binary;
using OSSMDotNet.Models;
using OSSMDotNet.ServiceDefaults;

namespace OSSMDotNet.ServiceBoundary.Tests;

public sealed class BufferBoundaryTests
{
    [Fact]
    public async Task FramesPreserveBoundariesAcrossConsecutiveMessages()
    {
        using var stream = new MemoryStream();
        await PipeFrames.WriteAsync(stream, new PipeWelcome(true), TestContext.Current.CancellationToken);
        await PipeFrames.WriteAsync(stream, new PipeWelcome(false), TestContext.Current.CancellationToken);
        stream.Position = 0;
        var first = await PipeFrames.ReadAsync(stream, TestContext.Current.CancellationToken);
        var second = await PipeFrames.ReadAsync(stream, TestContext.Current.CancellationToken);
        Assert.Equal("""{"accepted":true}"""u8.ToArray(), first);
        Assert.Equal("""{"accepted":false}"""u8.ToArray(), second);
        Assert.Equal(stream.Length, stream.Position);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(16_385)]
    public async Task InvalidFrameLengthIsRejectedBeforePayloadAllocation(int length)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, length);
        using var stream = new MemoryStream(header);
        await Assert.ThrowsAsync<InvalidDataException>(() => PipeFrames.ReadAsync(stream, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TruncatedPayloadCannotBecomeSuccessfulRequest()
    {
        using var stream = new MemoryStream([4, 0, 0, 0, 1]);
        await Assert.ThrowsAsync<EndOfStreamException>(() => PipeFrames.ReadAsync(stream, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void OperatorProofRequiresExactSecretRatherThanPrefixOrEncodingAlias()
    {
        var token = CapabilityTokens.NewToken();
        var hash = CapabilityTokens.Hash(token);
        Assert.True(CapabilityTokens.Matches(hash, token));
        Assert.False(CapabilityTokens.Matches(hash, token[..^1]));
        Assert.False(CapabilityTokens.Matches(hash, token + "="));
        Assert.False(CapabilityTokens.Matches(hash, new string('é', 43)));
        Assert.False(CapabilityTokens.Matches(hash, CapabilityTokens.NewToken()));
        Assert.False(CapabilityTokens.Matches(hash, null));
    }
}
