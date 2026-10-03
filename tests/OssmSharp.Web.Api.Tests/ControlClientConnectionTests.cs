using FastMoq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OssmSharp.Web.Client.Clients;
using OssmSharp.Web.Client.Connections;
using OssmSharp.Web.Client.Models;
using OssmSharp.Web.Contracts;
using Xunit;

namespace OssmSharp.Web.Api.Tests;

public sealed class ControlClientConnectionTests : MockerTestBase<UserControlClient>
{
    private static readonly string[] TransferFormats = ["Text", "Binary"];
    private ControlClientOptions _options = null!;

    protected override Action<Mocker> SetupMocksAction => mocks =>
    {
        _options = new() { ControlOrigin = "http://127.0.0.1:1", ServiceKey = new string('A', 43) };
        var options = Options.Create(_options);
        mocks.AddType<IOptions<ControlClientOptions>>(options);
        mocks.AddType(new ControlHubConnectionFactory(options));
    };

    [Fact]
    public void ResetIsNotExposedAsAUserCommand()
    {
        Assert.Null(typeof(OssmSharp.Web.Api.SignalR.ControlHub).GetMethod("Reset"));
        Assert.Null(typeof(UserControlClient).GetMethod("ResetAsync"));
    }

    [Theory]
    [InlineData(StatusCodes.Status401Unauthorized)]
    [InlineData(StatusCodes.Status503ServiceUnavailable)]
    [Trait("Category", "Integration")]
    public async Task FailedConnectionReturnsClientError(int statusCode)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        await using var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");
        app.Run(context =>
        {
            context.Response.StatusCode = statusCode;
            return Task.CompletedTask;
        });
        await app.StartAsync(TestContext.Current.CancellationToken);
        _options.ControlOrigin = app.Urls.Single();
        await using var client = Component;

        var error = await Assert.ThrowsAsync<ControlClientException>(
            () => client.GetStateAsync(TestContext.Current.CancellationToken));

        Assert.Equal("connection-unavailable", error.Code);
        var cause = Assert.IsType<HttpRequestException>(error.InnerException);
        Assert.Equal(statusCode, (int)cause.StatusCode!);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task FailedWebSocketUpgradeReturnsClientError()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        await using var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");
        app.MapPost("/hubs/control/v2/negotiate", () => Results.Json(new
        {
            negotiateVersion = 1,
            connectionId = "test",
            connectionToken = "test",
            availableTransports = new[] { new { transport = "WebSockets", transferFormats = TransferFormats } }
        }));
        app.MapGet("/hubs/control/v2", () => Results.Unauthorized());
        await app.StartAsync(TestContext.Current.CancellationToken);
        _options.ControlOrigin = app.Urls.Single();
        await using var client = Component;

        var error = await Assert.ThrowsAsync<ControlClientException>(
            () => client.GetStateAsync(TestContext.Current.CancellationToken));

        Assert.Equal("connection-unavailable", error.Code);
        Assert.IsType<AggregateException>(error.InnerException);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "Integration")]
    public async Task StopReachesServiceBeforePendingConnectCompletes(bool endGuest)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSignalR(options => options.MaximumParallelInvocationsPerClient = 2);
        builder.Services.AddSingleton(entered);
        await using var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");
        app.MapHub<BlockingControlHub>("/hubs/control/v2");
        await app.StartAsync(TestContext.Current.CancellationToken);
        _options.ControlOrigin = app.Urls.Single();
        await using var client = Component;
        using var pending = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var connect = client.ConnectAsync(pending.Token);
        try
        {
            await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
            var response = await (endGuest
                ? client.EndGuestAsync(TestContext.Current.CancellationToken)
                : client.StopAsync(TestContext.Current.CancellationToken));

            Assert.True(response.Accepted);
            Assert.False(connect.IsCompleted);
        }
        finally
        {
            await pending.CancelAsync();
            try { await connect; }
            catch (OperationCanceledException) { }
            catch (ControlClientUnknownOutcomeException) { }
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ConnectRemainsPendingBeyondCommandAcknowledgementDeadline()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pendingStop = new TaskCompletionSource<ControlResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSignalR(options => options.MaximumParallelInvocationsPerClient = 2);
        builder.Services.AddSingleton(entered);
        builder.Services.AddSingleton(pendingStop);
        await using var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");
        app.MapHub<BlockingControlHub>("/hubs/control/v2");
        await app.StartAsync(TestContext.Current.CancellationToken);
        _options.ControlOrigin = app.Urls.Single();
        _options.CommandTimeoutSeconds = 1;
        await using var client = Component;
        using var pending = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var connect = client.ConnectAsync(pending.Token);
        try
        {
            await entered.Task.WaitAsync(TestContext.Current.CancellationToken);

            await Assert.ThrowsAsync<ControlClientUnknownOutcomeException>(
                () => client.StopAsync(TestContext.Current.CancellationToken));

            Assert.False(connect.IsCompleted);
        }
        finally
        {
            await pending.CancelAsync();
            try { await connect; }
            catch (OperationCanceledException) { }
            catch (ControlClientUnknownOutcomeException) { }
        }
    }

    [Fact]
    public async Task CallerCancellationIsNotTranslatedToConnectionFailure()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await using var client = Component;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.GetStateAsync(cancellation.Token));
    }
}
