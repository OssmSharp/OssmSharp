using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using OSSMDotNet.Models;
using OSSMDotNet.GuestWeb.Services;
using OSSMDotNet.ServiceDefaults;

[assembly: CollectionBehavior(MaxParallelThreads = 4)]

namespace OSSMDotNet.GuestWeb.Tests;

public sealed class GuestSessionClientTests
{
    private const string SessionId = "test-session";
    private const string Invitation = "test-invitation-not-a-real-credential";
    internal const string ServiceToken = "test-service-token-not-a-real-credential";
    private const string GuestToken = "test-guest-token-not-a-real-credential";
    private static readonly GuestInput Input = new(2, "epoch-one", 1, "grant-one", 1, true, new(0, 10, 25, 40, 50));
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    private static readonly string[] expected = new[] { "join", "status", "input", "stop", "leave" };

    [Theory]
    [InlineData("guest-unauthorized")]
    [InlineData("session-expired")]
    public async Task RevokedGuestAuthorityEndsTheClientAndClosesItsPipe(string code)
    {
        await using var host = new FakeAdminPipe(async (pipe, cancellationToken) =>
        {
            await JoinOnServerAsync(pipe, cancellationToken);
            var call = await ReadCallAsync(pipe, cancellationToken);
            await ReplyAsync(pipe, call, cancellationToken, code, false);
            Assert.Equal(0, await pipe.ReadAsync(new byte[1], cancellationToken));
        });
        await using var client = host.CreateClient();
        Assert.True((await client.JoinAsync(SessionId, Invitation, Cancellation)).Ok);

        var result = await client.StatusAsync(Cancellation);

        Assert.False(result.Ok);
        Assert.True(client.IsExpired);
        Assert.False(client.IsJoined);
        Assert.False((await client.StatusAsync(Cancellation)).Ok);
        await host.CompleteAsync();
    }

    [Fact]
    public async Task StatusBeforeExplicitJoinDoesNotOpenPipe()
    {
        var pipeName = NewPipeName();
        await using var server = NewServer(pipeName);
        await using var client = FakeAdminPipe.CreateClient(pipeName);
        var result = await client.StatusAsync(Cancellation);

        Assert.False(result.Ok);
        Assert.Equal("not-joined", result.Code);
        Assert.False(server.IsConnected);
        Assert.False(client.IsJoined);
        Assert.False(client.IsExpired);
    }

    [Fact]
    public async Task GuestLifecycleOnlySendsScopedAllowedCallsAndKeepsGuestTokenPrivate()
    {
        var calls = new List<GuestCall>();
        await using var host = new FakeAdminPipe(async (pipe, cancellationToken) =>
        {
            var hello = await WelcomeAsync(pipe, cancellationToken);
            for (var index = 0; index < 5; index++)
            {
                var call = await ReadCallAsync(pipe, cancellationToken);
                calls.Add(call);
                Assert.Equal(hello.ConnectionId, call.ConnectionId);
                Assert.Equal(SessionId, call.SessionId);
                Assert.True(Guid.TryParseExact(call.RequestId, "N", out _));
                Assert.Equal(index == 0 ? Invitation : GuestToken, call.Proof);
                await ReplyAsync(pipe, call, cancellationToken, index == 2 ? "locked" : "ok", index != 2);
            }
        });
        await using var client = host.CreateClient();

        var joined = await client.JoinAsync(SessionId, Invitation, Cancellation);
        Assert.True(joined.Ok);
        Assert.True(client.IsJoined);
        Assert.DoesNotContain(GuestToken, JsonSerializer.Serialize(joined));
        Assert.True((await client.StatusAsync(Cancellation)).Ok);
        var rejected = await client.SubmitInputAsync(Input, Cancellation);
        Assert.False(rejected.Ok);
        Assert.Equal("locked", rejected.Code);
        Assert.True(client.IsJoined);
        Assert.True((await client.StopAsync(Cancellation)).Ok);
        Assert.True((await client.LeaveAsync(Cancellation)).Ok);
        await host.CompleteAsync();

        Assert.Equal(expected, calls.Select(call => call.Op));
        Assert.Equal(5, calls.Select(call => call.RequestId).Distinct().Count());
        Assert.Equal(Input, calls[2].Input);
        Assert.All(calls.Where(call => call.Op != "input"), call => Assert.Null(call.Input));
        Assert.False(client.IsJoined);
        Assert.True(client.IsExpired);
        var closed = Assert.Single(host.Logger.Collector.GetSnapshot());
        Assert.DoesNotContain(ServiceToken, closed.Message);
        Assert.DoesNotContain(GuestToken, closed.Message);
        Assert.DoesNotContain(Invitation, closed.Message);
    }

    [Fact]
    public async Task RejectedHandshakeClosesWithoutSendingInvitation()
    {
        await using var host = new FakeAdminPipe(async (pipe, cancellationToken) =>
        {
            await ReadAsync<PipeHello>(pipe, cancellationToken);
            await PipeFrames.WriteAsync(pipe, new PipeWelcome(false), cancellationToken);
            Assert.Equal(0, await pipe.ReadAsync(new byte[1], cancellationToken));
        });
        await using var client = host.CreateClient();

        var result = await client.JoinAsync(SessionId, Invitation, Cancellation);
        Assert.Equal("handshake-rejected", result.Code);
        Assert.False(result.Ok);
        Assert.True(client.IsExpired);
        await host.CompleteAsync();
    }

    [Theory]
    [InlineData("request-id")]
    [InlineData("version")]
    [InlineData("unknown-field")]
    [InlineData("missing-fields")]
    [InlineData("wrong-session")]
    [InlineData("wrong-connection")]
    [InlineData("trailing-json")]
    public async Task InvalidReplyExpiresConnectionAndNeverReplaysJoin(string defect)
    {
        await using var host = new FakeAdminPipe(async (pipe, cancellationToken) =>
        {
            await WelcomeAsync(pipe, cancellationToken);
            var call = await ReadCallAsync(pipe, cancellationToken);
            var reply = Reply(call);
            switch (defect)
            {
                case "request-id":
                    await PipeFrames.WriteAsync(pipe, reply with { RequestId = Guid.NewGuid().ToString("N") }, cancellationToken);
                    break;
                case "version":
                    await PipeFrames.WriteAsync(pipe, reply with { V = 2 }, cancellationToken);
                    break;
                case "unknown-field":
                    await PipeFrames.WriteAsync(pipe, new { reply.V, reply.RequestId, reply.Ok, reply.Code, reply.State, reply.Join, admin = true }, cancellationToken);
                    break;
                case "missing-fields":
                    await PipeFrames.WriteAsync(pipe, new { v = 1, requestId = call.RequestId, ok = true, code = "ok" }, cancellationToken);
                    break;
                case "wrong-session":
                    await PipeFrames.WriteAsync(pipe, reply with { Join = reply.Join! with { SessionId = "another-session" } }, cancellationToken);
                    break;
                case "wrong-connection":
                    await PipeFrames.WriteAsync(pipe, reply with { Join = reply.Join! with { ConnectionId = Guid.NewGuid().ToString("N") } }, cancellationToken);
                    break;
                case "trailing-json":
                    var bytes = JsonSerializer.SerializeToUtf8Bytes(reply, GuestProtocol.Json);
                    var frame = new byte[4 + bytes.Length + 2];
                    System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(0, 4), bytes.Length + 2);
                    bytes.AsSpan().CopyTo(frame.AsSpan(4));
                    "{}"u8.CopyTo(frame.AsSpan(4 + bytes.Length));
                    await pipe.WriteAsync(frame.AsMemory(), cancellationToken);
                    break;
                default:
                    Assert.Fail("Unknown test defect.");
                    break;
            }
            Assert.Equal(0, await pipe.ReadAsync(new byte[1], cancellationToken));
        });
        await using var client = host.CreateClient();

        Assert.Equal("invalid-reply", (await client.JoinAsync(SessionId, Invitation, Cancellation)).Code);
        await host.CompleteAsync();
        await using var replacement = NewServer(host.PipeName);
        Assert.Equal("invalid-reply", (await client.JoinAsync(SessionId, Invitation, Cancellation)).Code);
        Assert.False(replacement.IsConnected);
        Assert.True(client.IsExpired);
    }

    [Fact]
    public async Task LostInputReplyClosesPipeAndDoesNotReconnectOrReplay()
    {
        var received = new List<GuestCall>();
        await using var host = new FakeAdminPipe(async (pipe, cancellationToken) =>
        {
            await JoinOnServerAsync(pipe, cancellationToken);
            received.Add(await ReadCallAsync(pipe, cancellationToken));
        });
        await using var client = host.CreateClient();
        Assert.True((await client.JoinAsync(SessionId, Invitation, Cancellation)).Ok);

        Assert.Equal("pipe-disconnected", (await client.SubmitInputAsync(Input, Cancellation)).Code);
        await host.CompleteAsync();
        await using var replacement = NewServer(host.PipeName);
        Assert.Equal("pipe-disconnected", (await client.SubmitInputAsync(Input, Cancellation)).Code);
        Assert.Equal("pipe-disconnected", (await client.StatusAsync(Cancellation)).Code);
        Assert.False(replacement.IsConnected);
        Assert.Equal("input", Assert.Single(received).Op);
        Assert.True(client.IsExpired);
        Assert.False(client.IsJoined);
    }

    [Fact]
    public async Task StalledReplyHasTwoSecondDeadlineAndClosesPipe()
    {
        await using var host = new FakeAdminPipe(async (pipe, cancellationToken) =>
        {
            await JoinOnServerAsync(pipe, cancellationToken);
            Assert.Equal("status", (await ReadCallAsync(pipe, cancellationToken)).Op);
            Assert.Equal(0, await pipe.ReadAsync(new byte[1], cancellationToken));
        });
        await using var client = host.CreateClient();
        Assert.True((await client.JoinAsync(SessionId, Invitation, Cancellation)).Ok);
        var clock = Stopwatch.StartNew();

        var result = await client.StatusAsync(Cancellation);

        Assert.Equal("pipe-timeout", result.Code);
        Assert.InRange(clock.Elapsed, TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(5));
        Assert.True(client.IsExpired);
        await host.CompleteAsync();
    }

    [Fact]
    public async Task ConcurrentStatusAndInputAreSerializedWithoutOverlappingFrames()
    {
        var statusReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inputStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = new FakeAdminPipe(async (pipe, cancellationToken) =>
        {
            await JoinOnServerAsync(pipe, cancellationToken);
            var status = await ReadCallAsync(pipe, cancellationToken);
            Assert.Equal("status", status.Op);
            statusReceived.SetResult();
            await inputStarted.Task.WaitAsync(cancellationToken);
            using (var overlapProbe = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                overlapProbe.CancelAfter(TimeSpan.FromMilliseconds(150));
                await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                    await pipe.ReadExactlyAsync(new byte[1], overlapProbe.Token));
            }
            await ReplyAsync(pipe, status, cancellationToken);
            var input = await ReadCallAsync(pipe, cancellationToken);
            Assert.Equal("input", input.Op);
            await ReplyAsync(pipe, input, cancellationToken);
        });
        await using var client = host.CreateClient();
        Assert.True((await client.JoinAsync(SessionId, Invitation, Cancellation)).Ok);
        var statusTask = client.StatusAsync(Cancellation);
        await statusReceived.Task.WaitAsync(TimeSpan.FromSeconds(5), Cancellation);
        var inputTask = client.SubmitInputAsync(Input, Cancellation);
        inputStarted.SetResult();

        Assert.True((await statusTask).Ok);
        Assert.True((await inputTask).Ok);
        await host.CompleteAsync();
    }

    [Fact]
    public async Task CancellationDuringCallEndsConnectionInsteadOfResuming()
    {
        var statusReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = new FakeAdminPipe(async (pipe, cancellationToken) =>
        {
            await JoinOnServerAsync(pipe, cancellationToken);
            await ReadCallAsync(pipe, cancellationToken);
            statusReceived.SetResult();
            Assert.Equal(0, await pipe.ReadAsync(new byte[1], cancellationToken));
        });
        await using var client = host.CreateClient();
        Assert.True((await client.JoinAsync(SessionId, Invitation, Cancellation)).Ok);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
        var statusTask = client.StatusAsync(cancellation.Token);
        await statusReceived.Task.WaitAsync(TimeSpan.FromSeconds(5), Cancellation);

        cancellation.Cancel();

        Assert.Equal("request-cancelled", (await statusTask).Code);
        Assert.True(client.IsExpired);
        await host.CompleteAsync();
        Assert.Equal("request-cancelled", (await client.StatusAsync(Cancellation)).Code);
    }

    [Fact]
    public async Task DisposingJoinedClientClosesPipeAndInvalidatesLifecycle()
    {
        await using var host = new FakeAdminPipe(async (pipe, cancellationToken) =>
        {
            await JoinOnServerAsync(pipe, cancellationToken);
            Assert.Equal(0, await pipe.ReadAsync(new byte[1], cancellationToken));
        });
        var client = host.CreateClient();
        Assert.True((await client.JoinAsync(SessionId, Invitation, Cancellation)).Ok);

        await client.DisposeAsync();
        await client.DisposeAsync();

        await host.CompleteAsync();
        Assert.True(client.IsExpired);
        Assert.False((await client.StopAsync(Cancellation)).Ok);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("version")]
    [InlineData("epoch-empty")]
    [InlineData("epoch-too-long")]
    [InlineData("policy-version")]
    [InlineData("grant-empty")]
    [InlineData("grant-too-long")]
    [InlineData("sequence-zero")]
    [InlineData("sequence-too-large")]
    [InlineData("run-false")]
    [InlineData("desired-null")]
    public async Task InvalidInputIsRejectedBeforeWritingAndStopRemainsAvailable(string defect)
    {
        await using var host = new FakeAdminPipe(async (pipe, cancellationToken) =>
        {
            await JoinOnServerAsync(pipe, cancellationToken);
            var next = await ReadCallAsync(pipe, cancellationToken);
            Assert.Equal("stop", next.Op);
            await ReplyAsync(pipe, next, cancellationToken);
        });
        await using var client = host.CreateClient();
        Assert.True((await client.JoinAsync(SessionId, Invitation, Cancellation)).Ok);
        var invalid = defect switch
        {
            "null" => null!,
            "version" => Input with { V = 1 },
            "epoch-empty" => Input with { Epoch = "" },
            "epoch-too-long" => Input with { Epoch = new string('e', 65) },
            "policy-version" => Input with { PolicyVersion = 0 },
            "grant-empty" => Input with { GrantId = "" },
            "grant-too-long" => Input with { GrantId = new string('g', 65) },
            "sequence-zero" => Input with { Seq = 0 },
            "sequence-too-large" => Input with { Seq = 9_007_199_254_740_992 },
            "run-false" => Input with { Run = false },
            "desired-null" => Input with { Desired = null! },
            _ => throw new InvalidOperationException("Unknown test defect.")
        };

        var result = await client.SubmitInputAsync(invalid, Cancellation);

        Assert.False(result.Ok);
        Assert.Equal("invalid-input", result.Code);
        Assert.True(client.IsJoined);
        Assert.True((await client.StopAsync(Cancellation)).Ok);
        await host.CompleteAsync();
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("non-boolean")]
    [InlineData("unknown")]
    public async Task InvalidWelcomeClosesWithoutSendingInvitation(string defect)
    {
        await using var host = new FakeAdminPipe(async (pipe, cancellationToken) =>
        {
            await ReadAsync<PipeHello>(pipe, cancellationToken);
            switch (defect)
            {
                case "missing":
                    await PipeFrames.WriteAsync(pipe, new { }, cancellationToken);
                    break;
                case "non-boolean":
                    await PipeFrames.WriteAsync(pipe, new { accepted = "true" }, cancellationToken);
                    break;
                case "unknown":
                    await PipeFrames.WriteAsync(pipe, new { accepted = true, role = "guest" }, cancellationToken);
                    break;
                default:
                    Assert.Fail("Unknown test defect.");
                    break;
            }
            Assert.Equal(0, await pipe.ReadAsync(new byte[1], cancellationToken));
        });
        await using var client = host.CreateClient();

        var result = await client.JoinAsync(SessionId, Invitation, Cancellation);

        Assert.Equal("invalid-reply", result.Code);
        Assert.False(result.Ok);
        Assert.True(client.IsExpired);
        await host.CompleteAsync();
    }

    private static string NewPipeName() => $"guest-tests-{Guid.NewGuid():N}";

    private static NamedPipeServerStream NewServer(string name) =>
        new(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

    private static async Task<T> ReadAsync<T>(Stream pipe, CancellationToken cancellationToken) =>
        JsonSerializer.Deserialize<T>(await PipeFrames.ReadAsync(pipe, cancellationToken), GuestProtocol.Json)!;

    private static async Task<GuestCall> ReadCallAsync(Stream pipe, CancellationToken cancellationToken) =>
        GuestProtocol.DecodeCall(await PipeFrames.ReadAsync(pipe, cancellationToken));

    private static async Task<PipeHello> WelcomeAsync(Stream pipe, CancellationToken cancellationToken)
    {
        var hello = await ReadAsync<PipeHello>(pipe, cancellationToken);
        Assert.Equal(1, hello.V);
        Assert.Equal(ServiceToken, hello.ServiceToken);
        Assert.True(Guid.TryParseExact(hello.ConnectionId, "N", out _));
        await PipeFrames.WriteAsync(pipe, new PipeWelcome(true), cancellationToken);
        return hello;
    }

    private static async Task JoinOnServerAsync(Stream pipe, CancellationToken cancellationToken)
    {
        await WelcomeAsync(pipe, cancellationToken);
        var join = await ReadCallAsync(pipe, cancellationToken);
        Assert.Equal("join", join.Op);
        await ReplyAsync(pipe, join, cancellationToken);
    }

    private static GuestReply Reply(GuestCall call, string code = "ok", bool ok = true) =>
        new(1, call.RequestId, ok, code,
            new GuestStatus("locked", "pending-approval", 1, 1, true, false,
                new(0, 0, 1, 1, 50), null, null, new(30, 70), 1, "pending-approval", call.ConnectionId, 0, null),
            call.Op == "join" && ok ? new(SessionId, call.ConnectionId, GuestToken) : null);

    private static Task ReplyAsync(Stream pipe, GuestCall call, CancellationToken cancellationToken, string code = "ok", bool ok = true) =>
        PipeFrames.WriteAsync(pipe, Reply(call, code, ok), cancellationToken);

}
