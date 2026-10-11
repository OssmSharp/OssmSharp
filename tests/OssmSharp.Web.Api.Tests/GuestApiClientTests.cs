using FastMoq;
using FastMoq.Extensions;
using FastMoq.Providers;
using FastMoq.Providers.MoqProvider;
using Moq;
using OssmSharp.Web.Client.Clients;
using OssmSharp.Web.Client.Connections;
using OssmSharp.Web.Client.Models;
using OssmSharp.Web.Contracts;
using Xunit;

namespace OssmSharp.Web.Api.Tests;

public sealed class GuestApiClientTests : MockerTestBase<GuestApiClient>, IAsyncDisposable
{
    private Mock<IRpcClient> _connection = null!;

    protected override Action<Mocker> SetupMocksAction => mocks =>
    {
        _connection = mocks.GetOrCreateMock<IRpcClient>().AsMoq();
        _connection.Setup(client => client.StartSessionAsync("saved-credential", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _connection.Setup(client => client.DisposeAsync()).Returns(ValueTask.CompletedTask);
    };

    public async ValueTask DisposeAsync()
    {
        try { await Component.DisposeAsync(); }
        finally { base.Dispose(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SavedMetadataRejectsMissingExpiryAndContradictoryAvailability(bool contradictory)
    {
        var expiry = contradictory ? new DateTimeOffset(2030, 1, 2, 0, 0, 0, TimeSpan.Zero) : default;
        _connection.Setup(client => client.InvokeAsync<SavedGuestSession?>("GetSavedSession", It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SavedGuestSession(contradictory, expiry, true));

        await Assert.ThrowsAsync<InvalidDataException>(() => Component.GetSavedSessionAsync("saved-credential", TestContext.Current.CancellationToken));

        Mocks.Verify<IRpcClient>(client => client.InvokeAsync<ControlResponse>("Join", FastArg.Any<object?[]>(), FastArg.Any<CancellationToken>()), TimesSpec.NeverCalled);
    }

    [Fact]
    public async Task MissingAttachmentResultClosesTheConnectionWithAnExplicitRejection()
    {
        _connection.Setup(client => client.InvokeAsync<ControlResponse>("Join", It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ControlResponse)null!);

        var error = await Assert.ThrowsAsync<ApiClientException>(() => Component.ResumeAsync("saved-credential", TestContext.Current.CancellationToken));

        Assert.Equal("invalid-response", error.Code);
        Mocks.Verify<IRpcClient>(client => client.DisposeAsync(), TimesSpec.Once);
    }

    [Fact]
    public async Task MissingGuestStateIsAnExplicitInvalidResponse()
    {
        _connection.Setup(client => client.InvokeAsync<ControlResponse>("Join", It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ControlResponse("join", "joined", true));
        _connection.Setup(client => client.InvokeAsync<GuestSessionState>("GetGuestState", It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((GuestSessionState)null!);
        await Component.ResumeAsync("saved-credential", TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidDataException>(() => Component.GetStateAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("too-short")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA!")]
    public async Task UnusableRedemptionCredentialClosesTheExchange(string? credential)
    {
        _connection.Setup(client => client.StartAsync(It.IsAny<CancellationToken>(), "invitation"))
            .Returns(Task.CompletedTask);
        _connection.Setup(client => client.InvokeAsync<GuestRedemptionResponse>("Redeem", It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GuestRedemptionResponse("redeem", "invitation-redeemed", true, credential, new(2030, 1, 2, 0, 0, 0, TimeSpan.Zero)));

        await Assert.ThrowsAsync<InvalidDataException>(() => Component.RedeemAsync("invitation", TestContext.Current.CancellationToken));

        Mocks.Verify<IRpcClient>(client => client.DisposeAsync(), TimesSpec.Once);
        Mocks.Verify<IRpcClient>(client => client.StartSessionAsync(FastArg.Any<string>(), FastArg.Any<CancellationToken>()), TimesSpec.NeverCalled);
    }

    [Fact]
    public async Task MissingRedemptionResultCannotAppearAccepted()
    {
        _connection.Setup(client => client.StartAsync(It.IsAny<CancellationToken>(), "invitation"))
            .Returns(Task.CompletedTask);
        _connection.Setup(client => client.InvokeAsync<GuestRedemptionResponse>("Redeem", It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((GuestRedemptionResponse)null!);

        await Assert.ThrowsAsync<InvalidDataException>(() => Component.RedeemAsync("invitation", TestContext.Current.CancellationToken));

        Mocks.Verify<IRpcClient>(client => client.DisposeAsync(), TimesSpec.Once);
    }

    [Fact]
    public async Task SavedMetadataPreservesAbsoluteExpiryWithoutAttaching()
    {
        var expected = new SavedGuestSession(false, new(2030, 1, 2, 0, 0, 0, TimeSpan.Zero), true);
        _connection.Setup(client => client.InvokeAsync<SavedGuestSession?>("GetSavedSession", It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var actual = await Component.GetSavedSessionAsync("saved-credential", TestContext.Current.CancellationToken);

        Assert.Equal(expected, actual);
        Mocks.Verify<IRpcClient>(client => client.StartSessionAsync("saved-credential", FastArg.Any<CancellationToken>()), TimesSpec.Once);
        Mocks.Verify<IRpcClient>(client => client.InvokeAsync<ControlResponse>("Join", FastArg.Any<object?[]>(), FastArg.Any<CancellationToken>()), TimesSpec.NeverCalled);
    }
}
