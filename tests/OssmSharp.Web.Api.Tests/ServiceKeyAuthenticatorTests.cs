using AwesomeAssertions;
using FastMoq;
using FastMoq.Extensions;
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
