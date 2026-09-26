using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using OssmSharp.Web.Application.Models;

namespace OssmSharp.Web.Api.Tests;

/// <summary>
/// Verifies that service keys authenticate only the corresponding admin or guest role.
/// </summary>
public sealed class ServiceKeyAuthenticationTests
{
    [Fact]
    public async Task AuthorizationHeaderAuthenticatesEachConfiguredServiceAsItsOwnRole()
    {
        using var services = CreateServices();
        var admin = await AuthenticateAsync(services, $"ServiceKey {AdminKey}");
        var guest = await AuthenticateAsync(services, $"ServiceKey {GuestKey}");

        Assert.True(admin.Succeeded);
        Assert.True(guest.Succeeded);
        Assert.True(admin.Principal!.IsInRole(ControlServiceRoles.AdminWeb));
        Assert.False(admin.Principal.IsInRole(ControlServiceRoles.GuestWeb));
        Assert.True(guest.Principal!.IsInRole(ControlServiceRoles.GuestWeb));
        Assert.False(guest.Principal.IsInRole(ControlServiceRoles.AdminWeb));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Bearer aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("ServiceKey aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaab")]
    [InlineData("ServiceKey  aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public async Task MissingMalformedOrUnknownAuthorizationHeaderIsRejected(string authorization)
    {
        using var services = CreateServices();
        var result = await AuthenticateAsync(services, authorization);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task MultipleAuthorizationHeadersAreRejected()
    {
        using var services = CreateServices();
        using var scope = services.CreateScope();
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        context.Request.Headers.Append("Authorization", $"ServiceKey {AdminKey}");
        context.Request.Headers.Append("Authorization", $"ServiceKey {GuestKey}");

        var result = await context.AuthenticateAsync(ServiceKeyAuthenticationDefaults.Scheme);

        Assert.False(result.Succeeded);
    }

    private static async Task<AuthenticateResult> AuthenticateAsync(IServiceProvider services, string authorization)
    {
        using var scope = services.CreateScope();
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        if (authorization.Length > 0)
            context.Request.Headers.Authorization = authorization;
        return await context.AuthenticateAsync(ServiceKeyAuthenticationDefaults.Scheme);
    }

    private static ServiceProvider CreateServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.Configure<ControlServiceOptions>(options =>
        {
            options.AdminWebServiceKey = AdminKey;
            options.GuestWebServiceKey = GuestKey;
        });
        services.AddApiAuthentication();
        return services.BuildServiceProvider();
    }

    private const string AdminKey = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaA";
    private const string GuestKey = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbQ";
}
