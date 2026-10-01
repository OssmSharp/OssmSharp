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
    /// Registers validated control configuration.
    /// </summary>
    /// <param name="services">The host service collection to configure.</param>
    /// <returns>The service collection for further registration.</returns>
    public static IServiceCollection AddApiConfiguration(this IServiceCollection services)
    {
        services.AddSingleton<IValidateOptions<ControlServiceOptions>, ControlServiceOptionsValidator>();
        services.AddOptions<ControlServiceOptions>()
            .BindConfiguration("OSSM:Control", options => options.ErrorOnUnknownConfiguration = true)
            .ValidateOnStart();
        return services;
    }

    /// <summary>
    /// Registers one selected runtime device and the UI control session.
    /// </summary>
    /// <param name="services">The host service collection to configure.</param>
    /// <returns>The service collection for further registration.</returns>
    public static IServiceCollection AddApiComposition(this IServiceCollection services)
    {
        services.AddOemRuntime();
        services.AddSingleton<Func<string, IOssmDevice>>(provider =>
            provider.GetRequiredService<Func<string, DeviceBase>>());
        services.AddSingleton<IControlSession, ControlSession>();
        services.AddHostedService<DeviceLeaseManagementService>();
        return services;
    }

    /// <summary>
    /// Registers authentication for the separately configured AdminWeb and GuestWeb service keys.
    /// </summary>
    /// <param name="services">The host service collection to configure.</param>
    /// <returns>The service collection for further registration.</returns>
    public static IServiceCollection AddApiAuthentication(this IServiceCollection services)
    {
        services.AddSingleton<ServiceKeyAuthenticator>();
        services.AddAuthentication()
            .AddScheme<ServiceKeyAuthenticationOptions, ServiceKeyAuthenticationHandler>(
                ServiceKeyAuthenticationDefaults.Scheme, static _ => { });
        return services;
    }

    /// <summary>
    /// Registers distinct administrator, guest, and connection authorization policies.
    /// </summary>
    /// <param name="services">The host service collection to configure.</param>
    /// <returns>The service collection for further registration.</returns>
    public static IServiceCollection AddApiAuthorization(this IServiceCollection services)
    {
        services.AddAuthorization(options =>
        {
            options.AddPolicy(ControlServicePolicies.AdminWeb, policy => policy
                .AddAuthenticationSchemes(ServiceKeyAuthenticationDefaults.Scheme)
                .RequireAuthenticatedUser()
                .RequireRole(ControlServiceRoles.AdminWeb));
            options.AddPolicy(ControlServicePolicies.GuestWeb, policy => policy
                .AddAuthenticationSchemes(ServiceKeyAuthenticationDefaults.Scheme)
                .RequireAuthenticatedUser()
                .RequireRole(ControlServiceRoles.GuestWeb));
            options.AddPolicy(ControlServicePolicies.Service, policy => policy
                .AddAuthenticationSchemes(ServiceKeyAuthenticationDefaults.Scheme)
                .RequireAuthenticatedUser()
                .RequireRole(ControlServiceRoles.AdminWeb, ControlServiceRoles.GuestWeb));
        });
        return services;
    }

    /// <summary>
    /// Registers the bounded SignalR service transport without detailed errors or stateful reconnect.
    /// </summary>
    /// <param name="services">The host service collection to configure.</param>
    /// <returns>The service collection for further registration.</returns>
    public static IServiceCollection AddApiHub(this IServiceCollection services)
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
