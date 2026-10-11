using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using OssmSharp.Runtime.Oem;
using OssmSharp.Web.Api.Control;
using OssmSharp.Web.Api.Models;

namespace OssmSharp.Web.Api;

/// <summary>
/// Registers API configuration, authentication, authorization, and device composition.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers API configuration, authentication, authorization, the runtime device, and the UI control session.
    /// </summary>
    /// <param name="services">The host service collection to configure.</param>
    /// <returns>The service collection for further registration.</returns>
    public static IServiceCollection AddApiServices(this IServiceCollection services)
    {
        // Configuration
        services.AddSingleton<IValidateOptions<ControlServiceOptions>, ControlServiceOptionsValidator>();
        services.AddOptions<ControlServiceOptions>()
            .BindConfiguration("OSSM:Control", options => options.ErrorOnUnknownConfiguration = true)
            .ValidateOnStart();

        // Authentication and authorization services
        services.AddSingleton<ServiceKeyAuthenticator>();
        services.AddAuthentication()
            .AddScheme<ServiceKeyAuthenticationOptions, ServiceKeyAuthenticationHandler>(
                ServiceKeyAuthenticationDefaults.Scheme, static _ => { })
            .AddScheme<AuthenticationSchemeOptions, InvitationAuthenticationHandler>(
                InvitationAuthenticationHandler.SchemeName, static _ => { })
            .AddScheme<AuthenticationSchemeOptions, GuestSessionAuthenticationHandler>(
                GuestSessionAuthenticationHandler.SchemeName, static _ => { });

        services.AddAuthorization(options =>
        {
            options.AddPolicy(ControlServicePolicies.AdminWeb, policy => policy
                .AddAuthenticationSchemes(ServiceKeyAuthenticationDefaults.Scheme)
                .RequireAuthenticatedUser()
                .RequireRole(ControlServiceRoles.AdminWeb));
            options.AddPolicy(ControlServicePolicies.GuestWeb, policy => policy
                .AddAuthenticationSchemes(GuestSessionAuthenticationHandler.SchemeName)
                .RequireAuthenticatedUser()
                .RequireRole(ControlServiceRoles.GuestWeb));
            options.AddPolicy(ControlServicePolicies.Invitation, policy => policy
                .AddAuthenticationSchemes(InvitationAuthenticationHandler.SchemeName)
                .RequireAuthenticatedUser()
                .RequireRole(ControlServiceRoles.Invitation));
            options.AddPolicy(ControlServicePolicies.Service, policy => policy
                .AddAuthenticationSchemes(ServiceKeyAuthenticationDefaults.Scheme, InvitationAuthenticationHandler.SchemeName, GuestSessionAuthenticationHandler.SchemeName)
                .RequireAuthenticatedUser()
                .RequireRole(ControlServiceRoles.AdminWeb, ControlServiceRoles.GuestWeb, ControlServiceRoles.Invitation));
        });

        services.AddOssmSharpOemRuntime();

        services.AddSingleton<IControlSession, ControlSession>();
        services.AddHostedService<DeviceLeaseManagementService>();

        return services;
    }

    /// <summary>
    /// Registers the bounded SignalR service transport without detailed errors or stateful reconnect.
    /// </summary>
    /// <param name="services">The host service collection to configure.</param>
    /// <returns>The service collection for further registration.</returns>
    public static IServiceCollection AddSignalR(this IServiceCollection services)
    {
        services.AddSignalR(options =>
        {
            options.EnableDetailedErrors = false;
            options.DisableImplicitFromServicesParameters = true;
            options.MaximumReceiveMessageSize = 16_384;
            options.MaximumParallelInvocationsPerClient = 2;
            options.HandshakeTimeout = TimeSpan.FromSeconds(5);
            options.StatefulReconnectBufferSize = 0;
        });
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IConfigureOptions<HubOptions>, SignalRHubOptionsConfiguration>());
        return services;
    }
}
