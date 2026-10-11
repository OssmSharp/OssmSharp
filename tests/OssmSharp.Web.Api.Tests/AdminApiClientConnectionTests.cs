using System.Net;
using FastMoq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using OssmSharp.Web.Client.Clients;
using OssmSharp.Web.Client.Connections;
using OssmSharp.Web.Client.Models;
using OssmSharp.Web.Contracts;
using Xunit;

namespace OssmSharp.Web.Api.Tests;

public sealed class AdminApiClientConnectionTests : MockerTestBase<AdminApiClient>, IAsyncDisposable
{
    private static readonly string[] TransferFormats = ["Text", "Binary"];
    private RpcClientOptions _options = null!;
    private RpcClientTestScope _rpcScope = null!;

    protected override Action<Mocker> SetupMocksAction => mocks =>
    {
        _options = new() { EndpointUri = "http://127.0.0.1:1", ServiceKey = new string('A', 43) };
        _rpcScope = new(_options);
        mocks.AddType<IRpcClient>(_rpcScope.Connection);
    };

    public async ValueTask DisposeAsync()
    {
        try { await Component.DisposeAsync(); }
        finally
        {
            try { await _rpcScope.DisposeAsync(); }
            finally { base.Dispose(); }
        }
    }

    [Fact]
    public void ResetIsNotExposedAsAUserRequest()
    {
        Assert.Null(typeof(OssmSharp.Web.Api.SignalR.ControlHub).GetMethod("Reset"));
        Assert.Null(typeof(AdminApiClient).GetMethod("ResetAsync"));
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
        _options.EndpointUri = app.Urls.Single();
        await using var client = Component;

        var error = await Assert.ThrowsAsync<ApiClientException>(
            () => client.GetStateAsync(TestContext.Current.CancellationToken));

        Assert.Equal("connection-unavailable", error.Code);
        var cause = Assert.IsType<HttpRequestException>(error.InnerException);
        Assert.Equal((HttpStatusCode)statusCode, cause.StatusCode);
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
            availableTransports = new[] { new { transport = "WebSockets", transferFormats = TransferFormats } },
        }));
        app.MapGet("/hubs/control/v2", () => Results.Unauthorized());
        await app.StartAsync(TestContext.Current.CancellationToken);
        _options.EndpointUri = app.Urls.Single();
        await using var client = Component;

        var error = await Assert.ThrowsAsync<ApiClientException>(
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
        _options.EndpointUri = app.Urls.Single();
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
            catch (RpcClientRequestTimeoutException) { }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "Integration")]
    public async Task LongCommandRemainsPendingBeyondRequestAcknowledgementDeadline(bool resetPosition)
    {
        var options = new RpcClientOptions
        {
            EndpointUri = "http://127.0.0.1:1",
            ServiceKey = new string('A', 43),
            RequestTimeoutSeconds = 1,
        };
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
        options.EndpointUri = app.Urls.Single();
        await using var scope = new RpcClientTestScope(options);
        await using var client = new AdminApiClient(scope.Connection);
        using var pending = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var connect = resetPosition ? client.ResetPositionAsync(pending.Token) : client.ConnectAsync(pending.Token);
        try
        {
            await entered.Task.WaitAsync(TestContext.Current.CancellationToken);

            await Assert.ThrowsAsync<RpcClientRequestTimeoutException>(
                () => client.StopAsync(TestContext.Current.CancellationToken));

            Assert.False(connect.IsCompleted);
        }
        finally
        {
            await pending.CancelAsync();
            try { await connect; }
            catch (OperationCanceledException) { }
            catch (RpcClientRequestTimeoutException) { }
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

    [Fact]
    [Trait("Category", "Integration")]
    public async Task DisposalCancelsPendingConnectionStartup()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        await using var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");
        app.Run(context =>
        {
            entered.TrySetResult();
            return response.Task.WaitAsync(context.RequestAborted);
        });
        var token = TestContext.Current.CancellationToken;
        await app.StartAsync(token);
        _options.EndpointUri = app.Urls.Single();
        var startup = Component.GetStateAsync(token);
        await entered.Task.WaitAsync(token);
        var disposal = Component.DisposeAsync().AsTask();
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => startup.WaitAsync(TimeSpan.FromSeconds(5), token));
            await disposal.WaitAsync(TimeSpan.FromSeconds(5), token);
        }
        finally
        {
            response.TrySetResult();
            await disposal;
        }
    }
}
