using FastMoq;
using FastMoq.Providers.MoqProvider;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Moq;
using OssmSharp.Web.Api.Control;
using OssmSharp.Web.Api.SignalR;
using OssmSharp.Web.Contracts;
using Xunit;

namespace OssmSharp.Web.Api.Tests;

public sealed class ControlHubLoggingFilterTests : MockerTestBase<IHubFilter>, IAsyncDisposable
{
    private ControlHub _hub = null!;

    protected override Action<Mocker> SetupMocksAction => mocks =>
    {
        mocks.GetOrCreateMock<ILogger<ControlHub>>().AsMoq()
            .Setup(logger => logger.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
        mocks.AddType<IHubFilter>(new ControlHubLoggingFilter(mocks.GetRequiredObject<ILogger<ControlHub>>()));
        _hub = new ControlHub(mocks.GetRequiredObject<IControlSession>());
    };

    private (LogLevel Level, Dictionary<string, object?> Fields, Exception? Exception)[] Entries =>
        Mocks.GetOrCreateMock<ILogger<ControlHub>>().AsMoq().Invocations
        .Where(invocation => invocation.Method.Name == nameof(ILogger.Log))
        .Select(invocation => (
            (LogLevel)invocation.Arguments[0],
            Assert.IsAssignableFrom<IEnumerable<KeyValuePair<string, object?>>>(invocation.Arguments[2]).ToDictionary(),
            (Exception?)invocation.Arguments[3])).ToArray();

    public ValueTask DisposeAsync()
    {
        try
        {
            _hub.Dispose();
        }
        finally
        {
            base.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    [Theory]
    [InlineData(nameof(ControlHub.Submit))]
    [InlineData(nameof(ControlHub.Connect))]
    [InlineData(nameof(ControlHub.Redeem))]
    public async Task OperationsLogEntryAndResultWithoutCapabilitiesOrPayloads(string operation)
    {
        const string secret = "private-credential-must-not-be-logged";
        object response = operation switch
        {
            nameof(ControlHub.Submit) => new SettingsResponse(secret, SettingsResponseStatus.Completed, 5),
            nameof(ControlHub.Connect) => new ControlResponse(secret, "connected", true),
            _ => new GuestRedemptionResponse(secret, "invitation-redeemed", true, secret),
        };
        var context = Invocation(operation, [secret]);

        Assert.Same(response, await Component.InvokeMethodAsync(context, _ => ValueTask.FromResult<object?>(response)));

        var entries = Entries;
        Assert.Equal(2, entries.Length);
        Assert.All(entries, entry => Assert.Equal(LogLevel.Information, entry.Level));
        Assert.All(entries, entry => Assert.Equal(operation, entry.Fields["Operation"]));
        Assert.Equal(operation == nameof(ControlHub.Submit) ? "Completed" : "Accepted", entries[1].Fields["Result"]);
        Assert.True(Assert.IsType<double>(entries[1].Fields["DurationMilliseconds"]) >= 0);
        Assert.Equal(operation == nameof(ControlHub.Submit) ? 5 : (int?)null, entries[1].Fields["CommandsWritten"]);
        Assert.DoesNotContain(entries.SelectMany(entry => entry.Fields.Values), value => Equals(value, secret));
        Assert.DoesNotContain(entries.SelectMany(entry => entry.Fields.Keys), key => key is "SessionToken" or "Correlation" or "Arguments");
    }

    [Fact]
    public async Task PollingStaysQuietWhileRejectedSettingsRemainVisible()
    {
        await Component.InvokeMethodAsync(Invocation(nameof(ControlHub.GetGuestState)), _ => ValueTask.FromResult<object?>(null));
        Assert.All(Entries, entry => Assert.Equal(LogLevel.Trace, entry.Level));

        var response = new SettingsResponse("set", SettingsResponseStatus.Rejected, 0, "device-not-ready");
        await Component.InvokeMethodAsync(Invocation(nameof(ControlHub.Submit)), _ => ValueTask.FromResult<object?>(response));

        var finished = Entries[^1];
        Assert.Equal(LogLevel.Warning, finished.Level);
        Assert.Equal("Rejected", finished.Fields["Result"]);
        Assert.Equal("device-not-ready", finished.Fields["Code"]);
        Assert.Equal(0, finished.Fields["CommandsWritten"]);
    }

    [Fact]
    public async Task UnhandledFailureLogsTerminalResultAndPropagatesForFrameworkDiagnostics()
    {
        var failure = new IOException("Test connection failed.");
        var thrown = await Assert.ThrowsAsync<IOException>(() =>
            Component.InvokeMethodAsync(
                Invocation(nameof(ControlHub.Connect)),
                _ => ValueTask.FromException<object?>(failure)).AsTask());

        Assert.Same(failure, thrown);
        Assert.Equal(2, Entries.Length);
        Assert.Equal(LogLevel.Warning, Entries[1].Level);
        Assert.Equal("Faulted", Entries[1].Fields["Result"]);
        Assert.All(Entries, entry => Assert.Null(entry.Exception));
    }

    private HubInvocationContext Invocation(string method, IReadOnlyList<object?>? arguments = null) =>
        new(
            Mocks.GetRequiredObject<HubCallerContext>(),
            Mocks.GetRequiredObject<IServiceProvider>(),
            _hub,
            typeof(ControlHub).GetMethod(method)!,
            arguments ?? []);
}
