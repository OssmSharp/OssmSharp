using System.Net;
using Microsoft.AspNetCore.HostFiltering;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using OSSMDotNet.ControlService;
using OSSMDotNet.ControlService.Models;
using OSSMDotNet.Simulator;
using OSSMDotNet.Simulator.Models;

namespace OSSMDotNet.Api;

/// <summary>
/// Registers the API host, delivery boundary, and selected device composition.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers validated control configuration and rejects external listener overrides.
    /// </summary>
    /// <param name="services">The host service collection to configure.</param>
    /// <returns>The service collection for further registration.</returns>
    public static IServiceCollection AddApiConfiguration(this IServiceCollection services)
    {
        services.AddOptions<ControlServiceOptions>()
            .BindConfiguration("OSSM:Control", options => options.ErrorOnUnknownConfiguration = true)
            .ValidateDataAnnotations()
            .Validate<IConfiguration>((_, configuration) =>
                !configuration.GetSection("Kestrel").Exists() &&
                !configuration.GetValue<bool>(WebHostDefaults.PreferHostingUrlsKey),
                "Control listener overrides are not supported. Use OSSM:Control:ControlPort for the configured listener.")
            .ValidateOnStart();
        return services;
    }

    /// <summary>
    /// Registers the configured direct API listener and its optional HTTPS certificate.
    /// </summary>
    /// <param name="services">The host service collection to configure.</param>
    /// <returns>The service collection for further registration.</returns>
    public static IServiceCollection AddApiListener(this IServiceCollection services)
    {
        services.AddSingleton<IControlServiceCertificatePasswordProvider, EnvironmentCertificatePasswordProvider>();
        services.AddOptions<KestrelServerOptions>()
            .Configure<IOptions<ControlServiceOptions>, IControlServiceCertificatePasswordProvider>((server, options, passwords) =>
            {
                var configuration = options.Value;
                var address = IPAddress.Parse(configuration.BindAddress);
                server.Listen(address, configuration.ControlPort, endpoint =>
                {
                    if (configuration.HttpsCertificatePath.Length > 0)
                        endpoint.UseHttps(configuration.HttpsCertificatePath, passwords.GetPassword());
                });
                server.Limits.MaxRequestBodySize = 16_384;
            });
        return services;
    }

    /// <summary>
    /// Registers one selected simulator, application authority, and API delivery lifetime.
    /// </summary>
    /// <param name="services">The host service collection to configure.</param>
    /// <returns>The service collection for further registration.</returns>
    public static IServiceCollection AddApiComposition(this IServiceCollection services)
    {
        services.AddOssmSimulator(_ => { });
        services.AddOptions<SimulatedDeviceOptions>()
            .Configure<IOptions<ControlServiceOptions>>((simulator, control) =>
                simulator.ExecutablePath = control.Value.SimulatorPath);
        services.AddControlService();
        services.AddSingleton<ControlHubConnectionRegistry>();
        services.AddSingleton<SimulatorFaultOperations>();
        services.AddSingleton<IControlHubPushSink, SignalRControlHubPushSink>();
        services.AddSingleton<ControlPushDispatcher>();
        services.AddHostedService<DeviceLoop>();
        services.AddHostedService(provider => provider.GetRequiredService<ControlPushDispatcher>());
        return services;
    }

    /// <summary>
    /// Registers direct Host filtering as DNS-rebinding protection, independent of service authentication.
    /// </summary>
    /// <param name="services">The host service collection to configure.</param>
    /// <returns>The service collection for further registration.</returns>
    public static IServiceCollection AddApiHostFiltering(this IServiceCollection services)
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IConfigureOptions<HostFilteringOptions>, ControlHostFilteringOptionsConfiguration>());
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
        services.AddProblemDetails();
        services.AddSignalR(options =>
        {
            options.EnableDetailedErrors = false;
            options.DisableImplicitFromServicesParameters = true;
            options.MaximumReceiveMessageSize = 16_384;
            options.MaximumParallelInvocationsPerClient = 1;
            options.HandshakeTimeout = TimeSpan.FromSeconds(5);
            options.StatefulReconnectBufferSize = 0;
        });
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IConfigureOptions<HubOptions>, SignalRHubOptionsConfiguration>());
        return services;
    }
}
