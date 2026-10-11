using FastMoq;
using FastMoq.Extensions;
using FastMoq.Providers.MoqProvider;
using Moq;
using OssmSharp.Web.Client.Clients;
using OssmSharp.Web.Client.Connections;
using OssmSharp.Web.Client.Models;
using OssmSharp.Web.Contracts;
using Xunit;

namespace OssmSharp.Web.Api.Tests;

public sealed class AdminApiClientTests : MockerTestBase<AdminApiClient>, IAsyncDisposable
{
    private Mock<IRpcClient> _connection = null!;

    protected override Action<Mocker> SetupMocksAction => mocks =>
    {
        _connection = mocks.GetOrCreateMock<IRpcClient>().AsMoq();
        _connection.Setup(client => client.StartAsync(It.IsAny<CancellationToken>(), null))
            .Returns(Task.CompletedTask);
        _connection.Setup(client => client.DisposeAsync()).Returns(ValueTask.CompletedTask);
    };

    public async ValueTask DisposeAsync()
    {
        try { await Component.DisposeAsync(); }
        finally { base.Dispose(); }
    }

    [Fact]
    public async Task MissingUserStateHasAnExplicitClientError()
    {
        _connection.Setup(client => client.InvokeAsync<UserSessionState>("GetUserState", It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((UserSessionState)null!);

        var error = await Assert.ThrowsAsync<ApiClientException>(
            () => Component.GetStateAsync(TestContext.Current.CancellationToken));

        Assert.Equal("invalid-response", error.Code);
    }

    [Fact]
    public async Task InvitationRefreshRequiresAnAcknowledgement()
    {
        _connection.Setup(client => client.InvokeAsync<ControlResponse>("RefreshInvitation", It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ControlResponse)null!);

        var error = await Assert.ThrowsAsync<ApiClientException>(
            () => Component.RefreshInvitationAsync(TestContext.Current.CancellationToken));

        Assert.Equal("invalid-response", error.Code);
    }
}
