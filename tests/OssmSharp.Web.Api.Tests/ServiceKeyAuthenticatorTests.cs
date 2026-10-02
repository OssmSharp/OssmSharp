using System.Security.Claims;
using AwesomeAssertions;
using FastMoq;
using FastMoq.Extensions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using OssmSharp.Web.Api.Authentication;
using OssmSharp.Web.Api.Models;
using OssmSharp.Web.Client.DependencyInjection;
using OssmSharp.Web.Client.Models;
using Xunit;

namespace OssmSharp.Web.Api.Tests;

public sealed class ServiceKeyAuthenticatorTests : MockerTestBase<ServiceKeyAuthenticator>
{
    private const string AdminKey = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string GuestKey = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBE";

    protected override Action<Mocker> SetupMocksAction => mocks =>
        mocks.SetupOptions(new ControlServiceOptions
        {
            AdminWebServiceKey = AdminKey
        });

    [Theory]
    [InlineData("ServiceKey")]
    [InlineData("servicekey AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("Bearer AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("ServiceKey invalid!")]
    [InlineData("ServiceKey CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC")]
    public void AuthenticateRejectsInvalidHeader(string value)
    {
        var identity = Component.Authenticate(value);

        Assert.Null(identity);
    }

    [Fact]
    public void AuthenticateRejectsMissingHeader()
    {
        var identity = Component.Authenticate(StringValues.Empty);

        Assert.Null(identity);
    }

    [Fact]
    public void AuthenticateRejectsMultipleHeaders()
    {
        var identity = Component.Authenticate(new StringValues([
            $"ServiceKey {AdminKey}",
            $"ServiceKey {GuestKey}"
        ]));

        Assert.Null(identity);
    }

    [Theory]
    [InlineData(AdminKey, "admin-web", ControlServiceRoles.AdminWeb)]
    public void AuthenticateReturnsConfiguredServiceIdentity(string key, string name, string role)
    {
        var identity = Component.Authenticate($"ServiceKey {key}");

        identity.Should().Be(new ServiceIdentity(name, role));
    }

    [Fact]
    public void AuthenticateRejectsGuestServiceKey()
    {
        Assert.Null(Component.Authenticate($"ServiceKey {GuestKey}"));
    }
}

public sealed class ServiceAuthorizationTests
{
    [Fact]
    public async Task AuthorizationPoliciesSeparateServiceRoles()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddApiAuthorization();
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
            ["Client:ControlOrigin"] = "http://127.0.0.1:5003",
            ["Client:ServiceKey"] = key
        }).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        if (guest)
            services.AddGuestControlClient("Client");
        else
            services.AddControlClient("Client");
        using var provider = services.BuildServiceProvider();
        if (valid)
            Assert.Equal(key, provider.GetRequiredService<IOptions<ControlClientOptions>>().Value.ServiceKey);
        else
            Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<ControlClientOptions>>().Value);
    }

    private static ClaimsPrincipal Principal(string role) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.Role, role)], "test", ClaimTypes.Name, ClaimTypes.Role));
}
