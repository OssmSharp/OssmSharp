using System.Security.Claims;
using AwesomeAssertions;
using FastMoq;
using FastMoq.Extensions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using OssmSharp.Web.Api.Authentication;
using OssmSharp.Web.Api.Models;
using Xunit;

namespace OssmSharp.Web.Api.Tests;

public sealed class ServiceKeyAuthenticatorTests : MockerTestBase<ServiceKeyAuthenticator>
{
    private const string AdminKey = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string GuestKey = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBE";

    protected override Action<Mocker> SetupMocksAction => mocks =>
        mocks.SetupOptions(new ControlServiceOptions
        {
            AdminWebServiceKey = AdminKey,
            GuestWebServiceKey = GuestKey
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
    [InlineData(GuestKey, "guest-web", ControlServiceRoles.GuestWeb)]
    public void AuthenticateReturnsConfiguredServiceIdentity(string key, string name, string role)
    {
        var identity = Component.Authenticate($"ServiceKey {key}");

        identity.Should().Be(new ServiceIdentity(name, role));
    }

    [Fact]
    public void AuthenticateRejectsKeyMatchingBothServices()
    {
        using var authenticator = new ServiceKeyAuthenticator(Options.Create(new ControlServiceOptions
        {
            AdminWebServiceKey = AdminKey,
            GuestWebServiceKey = AdminKey
        }));

        var identity = authenticator.Authenticate($"ServiceKey {AdminKey}");

        Assert.Null(identity);
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

    private static ClaimsPrincipal Principal(string role) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.Role, role)], "test", ClaimTypes.Name, ClaimTypes.Role));
}
