using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;
using OSSMDotNet.ControlService.Models;

namespace OSSMDotNet.Api.Tests;

/// <summary>
/// Checks credential parsing and role assignment at the service authentication boundary.
/// </summary>
public sealed class ServiceKeyAuthenticationBoundaryTests
{
    private const string AdminKey = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaA";
    private const string GuestKey = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbQ";

    public static IEnumerable<object?[]> InvalidAuthorizationHeaders =>
    [
        [Array.Empty<string>()],
        [new[] { "Bearer " + AdminKey }],
        [new[] { "ServiceKey short" }],
        [new[] { "ServiceKey " + new string('a', 42) + "B" }],
        [new[] { "ServiceKey " + AdminKey, "ServiceKey " + GuestKey }]
    ];

    [Theory]
    [MemberData(nameof(InvalidAuthorizationHeaders))]
    public async Task ZeroMalformedUnknownOrAmbiguousAuthorizationHeadersCreateNoPrincipal(string[] headers)
    {
        using var services = CreateServices();

        var result = await AuthenticateAsync(services, headers);

        Assert.False(result.Succeeded);
        Assert.Null(result.Principal);
        Assert.Contains("Invalid service credentials", result.Failure!.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(AdminKey, "admin-web", ControlServiceRoles.AdminWeb, ControlServiceRoles.GuestWeb)]
    [InlineData(GuestKey, "guest-web", ControlServiceRoles.GuestWeb, ControlServiceRoles.AdminWeb)]
    public async Task EachConfiguredServiceKeyCreatesOnlyItsConfiguredRole(
        string key, string name, string allowedRole, string deniedRole)
    {
        using var services = CreateServices();

        var result = await AuthenticateAsync(services, [$"ServiceKey {key}"]);
        var principal = Assert.IsType<ClaimsPrincipal>(result.Principal);

        Assert.True(result.Succeeded);
        Assert.Equal(name, principal.FindFirstValue(ClaimTypes.NameIdentifier));
        Assert.True(principal.IsInRole(allowedRole));
        Assert.False(principal.IsInRole(deniedRole));
        Assert.Equal(ServiceKeyAuthenticationDefaults.Scheme, principal.Identity!.AuthenticationType);
    }

    [Fact]
    public void DisposedAuthenticatorCannotAuthenticateAFormerlyValidCredential()
    {
        using var services = CreateServices();
        var authenticator = services.GetRequiredService<ServiceKeyAuthenticator>();

        Assert.Equal(ControlServiceRoles.AdminWeb,
            authenticator.Authenticate($"ServiceKey {AdminKey}")!.Role);
        authenticator.Dispose();

        Assert.Null(authenticator.Authenticate($"ServiceKey {AdminKey}"));
        Assert.Null(authenticator.Authenticate($"ServiceKey {GuestKey}"));
    }

    private static async Task<AuthenticateResult> AuthenticateAsync(IServiceProvider services, IEnumerable<string> headers)
    {
        using var scope = services.CreateScope();
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        foreach (var header in headers)
            context.Request.Headers.Append("Authorization", header);
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
}
