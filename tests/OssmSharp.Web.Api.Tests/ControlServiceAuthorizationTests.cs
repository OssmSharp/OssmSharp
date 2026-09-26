using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace OssmSharp.Web.Api.Tests;

/// <summary>
/// Verifies that service roles satisfy only their assigned hub authorization policies.
/// </summary>
public sealed class ControlServiceAuthorizationTests
{
    [Fact]
    public async Task RolePoliciesSeparateAdminAndGuestAuthorityWhileAllowingBothToConnect()
    {
        using var services = CreateServices();
        var authorization = services.GetRequiredService<IAuthorizationService>();
        var admin = CreatePrincipal(ControlServiceRoles.AdminWeb);
        var guest = CreatePrincipal(ControlServiceRoles.GuestWeb);

        Assert.True((await authorization.AuthorizeAsync(admin, null, ControlServicePolicies.AdminWeb)).Succeeded);
        Assert.False((await authorization.AuthorizeAsync(guest, null, ControlServicePolicies.AdminWeb)).Succeeded);
        Assert.True((await authorization.AuthorizeAsync(guest, null, ControlServicePolicies.GuestWeb)).Succeeded);
        Assert.False((await authorization.AuthorizeAsync(admin, null, ControlServicePolicies.GuestWeb)).Succeeded);
        Assert.True((await authorization.AuthorizeAsync(admin, null, ControlServicePolicies.Service)).Succeeded);
        Assert.True((await authorization.AuthorizeAsync(guest, null, ControlServicePolicies.Service)).Succeeded);
    }

    [Fact]
    public async Task UnauthenticatedPrincipalsCannotSatisfyAnyServicePolicy()
    {
        using var services = CreateServices();
        var authorization = services.GetRequiredService<IAuthorizationService>();
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());

        Assert.False((await authorization.AuthorizeAsync(anonymous, null, ControlServicePolicies.AdminWeb)).Succeeded);
        Assert.False((await authorization.AuthorizeAsync(anonymous, null, ControlServicePolicies.GuestWeb)).Succeeded);
        Assert.False((await authorization.AuthorizeAsync(anonymous, null, ControlServicePolicies.Service)).Succeeded);
    }

    private static ClaimsPrincipal CreatePrincipal(string role) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.Role, role)], ServiceKeyAuthenticationDefaults.Scheme));

    private static ServiceProvider CreateServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddApiAuthorization();
        return services.BuildServiceProvider();
    }
}
