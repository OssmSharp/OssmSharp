using System.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HostFiltering;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using OSSMDotNet.ControlCore;
using OSSMDotNet.ControlService.Models;
using OSSMDotNet.DeviceBridge;
using OSSMDotNet.Models;

namespace OSSMDotNet.ControlService;

/// <summary>Registers the private control-service authority, authentication, and SignalR boundary.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>Registers and validates private control-service configuration.</summary>
    public static IServiceCollection AddControlServiceConfiguration(this IServiceCollection services)
    {
        services.AddOptions<ControlServiceOptions>()
            .BindConfiguration("OSSM:Control", options => options.ErrorOnUnknownConfiguration = true)
            .ValidateDataAnnotations()
            .Validate<IConfiguration>((_, configuration) =>
                !configuration.GetSection("Kestrel").Exists() &&
                !configuration.GetValue<bool>(WebHostDefaults.PreferHostingUrlsKey),
                "Control listener overrides are not supported. Use OSSM:Control:ControlPort for the 127.0.0.1 listener.")
            .ValidateOnStart();
        return services;
    }

    /// <summary>Registers the explicitly configured direct control-service listener.</summary>
    public static IServiceCollection AddControlServiceListener(this IServiceCollection services)
    {
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

    /// <summary>Registers the control engine, simulator bridge, activity log, and device loop.</summary>
    public static IServiceCollection AddControlServiceAuthority(this IServiceCollection services)
    {
        services.AddControlActivity();
        services.AddSingleton<IControlServiceCertificatePasswordProvider, EnvironmentCertificatePasswordProvider>();
        services.AddSingleton<SimulatorProcessBridge>();
        services.AddSingleton<IDeviceBridge>(provider => provider.GetRequiredService<SimulatorProcessBridge>());
        services.AddSingleton<PrototypeEngine>();
        services.AddSingleton<ControlAuthority>();
        services.AddSingleton<ControlHubConnectionRegistry>();
        services.AddSingleton<GuestControlOperations>();
        services.AddSingleton<IControlOperations, ControlOperations>();
        services.AddSingleton<IControlHubPushSink, SignalRControlHubPushSink>();
        services.AddSingleton<ControlPushDispatcher>();
        services.AddHostedService<DeviceLoop>();
        services.AddHostedService(provider => provider.GetRequiredService<ControlPushDispatcher>());
        return services;
    }

    /// <summary>Registers bounded operator-visible activity logging.</summary>
    public static IServiceCollection AddControlActivity(this IServiceCollection services)
    {
        services.AddSingleton<ControlActivityProvider>();
        services.AddSingleton<ILoggerProvider>(provider => provider.GetRequiredService<ControlActivityProvider>());
        return services;
    }

    /// <summary>Registers direct Host filtering as DNS-rebinding protection, independent of service authentication.</summary>
    public static IServiceCollection AddControlServiceHostFiltering(this IServiceCollection services)
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IConfigureOptions<HostFilteringOptions>, ControlHostFilteringOptionsConfiguration>());
        return services;
    }

    /// <summary>Registers authentication for the separately configured AdminWeb and GuestWeb service keys.</summary>
    public static IServiceCollection AddControlServiceAuthentication(this IServiceCollection services)
    {
        services.AddSingleton<ServiceKeyAuthenticator>();
        services.AddAuthentication()
            .AddScheme<ServiceKeyAuthenticationOptions, ServiceKeyAuthenticationHandler>(
                ServiceKeyAuthenticationDefaults.Scheme, static _ => { });
        return services;
    }

    /// <summary>Registers distinct administrator, guest, and connection authorization policies.</summary>
    public static IServiceCollection AddControlServiceAuthorization(this IServiceCollection services)
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

    /// <summary>Registers the hardened SignalR service transport.</summary>
    public static IServiceCollection AddControlServiceHub(this IServiceCollection services)
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
