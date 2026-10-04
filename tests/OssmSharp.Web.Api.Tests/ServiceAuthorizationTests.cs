using System.Security.Claims;
using FastMoq;
using FastMoq.Extensions;
using FastMoq.Providers;
using FastMoq.Providers.MoqProvider;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Moq;
using OssmSharp.Web.Api.Authentication;
using OssmSharp.Web.Client.Clients;
using OssmSharp.Web.Client.Connections;
using OssmSharp.Web.Client.DependencyInjection;
using OssmSharp.Web.Client.Models;
using OssmSharp.Web.Contracts;
using Shiny.BluetoothLE;
using Xunit;

namespace OssmSharp.Web.Api.Tests;

public sealed class ServiceAuthorizationTests
{
    [Fact]
    public async Task AuthorizationPoliciesSeparateServiceRoles()
    {
        using var mocks = new Mocker();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSingleton(mocks.GetOrCreateMock<IBleManager>().Instance);
        services.AddApiServices();
        await using var provider = services.BuildServiceProvider();
        var authorization = provider.GetRequiredService<IAuthorizationService>();
        var admin = Principal(ControlServiceRoles.AdminWeb);
        var guest = Principal(ControlServiceRoles.GuestWeb);

        Assert.True((await authorization.AuthorizeAsync(admin, null, ControlServicePolicies.AdminWeb)).Succeeded);
        Assert.False((await authorization.AuthorizeAsync(guest, null, ControlServicePolicies.AdminWeb)).Succeeded);
        Assert.True((await authorization.AuthorizeAsync(guest, null, ControlServicePolicies.GuestWeb)).Succeeded);
        Assert.False((await authorization.AuthorizeAsync(admin, null, ControlServicePolicies.GuestWeb)).Succeeded);
        Assert.True((await authorization.AuthorizeAsync(admin, null, ControlServicePolicies.Service)).Succeeded);
        Assert.True((await authorization.AuthorizeAsync(guest, null, ControlServicePolicies.Service)).Succeeded);
    }

    [Theory]
    [InlineData(true, "", true)]
    [InlineData(true, "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", false)]
    [InlineData(false, "", false)]
    [InlineData(false, "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", true)]
    [InlineData(false, "invalid", false)]
    public void ClientRegistrationSeparatesGuestAndAdminCredentials(bool guest, string key, bool valid)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Client:EndpointUri"] = "http://127.0.0.1:5003",
            ["Client:ServiceKey"] = key,
        }).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        if (guest)
            services.AddGuestApiClient("Client");
        else
            services.AddAdminApiClient("Client");
        using var provider = services.BuildServiceProvider();
        if (valid)
            Assert.Equal(key, provider.GetRequiredService<IOptions<RpcClientOptions>>().Value.ServiceKey);
        else
            Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<RpcClientOptions>>().Value);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ClientInterfacesShareScopedImplementations(bool guest)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Client:EndpointUri"] = "http://127.0.0.1:5003",
            ["Client:ServiceKey"] = guest ? string.Empty : new string('A', 43),
        }).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        if (guest)
            services.AddGuestApiClient("Client");
        else
            services.AddAdminApiClient("Client");

        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        await using var firstScope = provider.CreateAsyncScope();
        await using var secondScope = provider.CreateAsyncScope();
        Assert.Same(provider.GetRequiredService<SignalRHubConnectionFactory>(), provider.GetRequiredService<IHubConnectionFactory>());
        Assert.Same(firstScope.ServiceProvider.GetRequiredService<IHubConnectionFactory>(), secondScope.ServiceProvider.GetRequiredService<IHubConnectionFactory>());
        Assert.NotSame(firstScope.ServiceProvider.GetRequiredService<IRpcClient>(), secondScope.ServiceProvider.GetRequiredService<IRpcClient>());
        if (guest)
        {
            var client = firstScope.ServiceProvider.GetRequiredService<IGuestApiClient>();
            Assert.Same(firstScope.ServiceProvider.GetRequiredService<GuestApiClient>(), client);
            Assert.NotSame(client, secondScope.ServiceProvider.GetRequiredService<IGuestApiClient>());
        }
        else
        {
            var client = firstScope.ServiceProvider.GetRequiredService<IAdminApiClient>();
            Assert.Same(firstScope.ServiceProvider.GetRequiredService<AdminApiClient>(), client);
            Assert.NotSame(client, secondScope.ServiceProvider.GetRequiredService<IAdminApiClient>());
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ApiClientRegistrationsSendStopThroughScopedRpcClient(bool guest)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var invitation = new string('A', 43);
        var response = new ControlResponse("test", "accepted", true);
        using var mocks = new Mocker();
        var connection = mocks.GetOrCreateMock<IRpcClient>();
        connection.AsMoq()
            .Setup(rpc => rpc.StartAsync(cancellationToken, guest ? invitation : null))
            .Returns(Task.CompletedTask);
        connection.AsMoq()
            .Setup(rpc => rpc.InvokeAsync<ControlResponse>(It.IsAny<string>(), It.IsAny<object?[]>(), cancellationToken))
            .ReturnsAsync(response);
        var services = new ServiceCollection();
        if (guest)
            services.AddGuestApiClient("Client");
        else
            services.AddAdminApiClient("Client");
        services.AddScoped<IRpcClient>(_ => connection.Instance);
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        await using var scope = provider.CreateAsyncScope();

        ControlResponse actual;
        if (guest)
        {
            var client = scope.ServiceProvider.GetRequiredService<IGuestApiClient>();
            await client.JoinAsync(invitation, cancellationToken);
            actual = await client.StopAsync(cancellationToken);
        }
        else
        {
            var client = scope.ServiceProvider.GetRequiredService<IAdminApiClient>();
            actual = await client.StopAsync(cancellationToken);
        }

        Assert.Same(response, actual);
        mocks.Verify<IRpcClient>(rpc => rpc.StartAsync(cancellationToken, guest ? invitation : null), TimesSpec.Once);
        var method = guest ? "GuestStop" : "Stop";
        mocks.Verify<IRpcClient>(
            rpc => rpc.InvokeAsync<ControlResponse>(
                method,
                FastArg.Is<object?[]>(arguments => arguments.Length == 1 && arguments[0] is string),
                cancellationToken),
            TimesSpec.Once);
    }

    private static ClaimsPrincipal Principal(string role) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.Role, role)], "test", ClaimTypes.Name, ClaimTypes.Role));
}
