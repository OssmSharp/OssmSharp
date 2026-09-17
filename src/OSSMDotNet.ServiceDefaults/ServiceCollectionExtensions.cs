using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using System.Net;
using System.Text.Json.Serialization;

namespace OSSMDotNet.ServiceDefaults;

/// <summary>Registers validated local host configuration and shared Blazor hosting services.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>Registers and validates the loopback-only AdminWeb configuration.</summary>
    public static IServiceCollection AddAdminConfiguration(this IServiceCollection services)
    {
        services.AddOptions<AdminOptions>()
            .BindConfiguration("OSSM:Admin", options => options.ErrorOnUnknownConfiguration = true)
            .ValidateDataAnnotations()
            .Validate<IConfiguration>((_, configuration) =>
                !configuration.GetSection("Kestrel").Exists() &&
                !configuration.GetValue<bool>(WebHostDefaults.PreferHostingUrlsKey),
                "Admin listener overrides are not supported. Use OSSM:Admin:AdminPort for the 127.0.0.1 listener.")
            .ValidateOnStart();
        return services;
    }

    /// <summary>Registers and validates the local guest service configuration.</summary>
    public static IServiceCollection AddGuestConfiguration(this IServiceCollection services)
    {
        services.AddOptions<GuestOptions>()
            .BindConfiguration("OSSM:Guest", options => options.ErrorOnUnknownConfiguration = true)
            .ValidateDataAnnotations()
            .ValidateOnStart();
        return services;
    }

    /// <summary>Registers the fixed loopback listener for the unauthenticated admin web host.</summary>
    public static IServiceCollection AddAdminListener(this IServiceCollection services)
    {
        services.AddOptions<KestrelServerOptions>()
            .Configure<IOptions<AdminOptions>>((server, options) =>
            {
                server.Listen(IPAddress.Loopback, options.Value.AdminPort);
                server.Limits.MaxRequestBodySize = 16_384;
            });
        return services;
    }

    /// <summary>Registers the constrained interactive Blazor host defaults.</summary>
    public static IServiceCollection AddLocalBlazor(this IServiceCollection services)
    {
        services.AddProblemDetails();
        services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = false);
        services.TryAddSingleton(TimeProvider.System);
        services.AddDataProtection().UseEphemeralDataProtectionProvider();
        services.AddRazorComponents().AddInteractiveServerComponents(options =>
        {
            options.DetailedErrors = false;
            options.DisconnectedCircuitRetentionPeriod = TimeSpan.Zero;
            options.JSInteropDefaultCallTimeout = TimeSpan.FromSeconds(2);
        });
        services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.PropertyNameCaseInsensitive = false;
            options.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
            options.SerializerOptions.AllowDuplicateProperties = false;
            options.SerializerOptions.RespectRequiredConstructorParameters = true;
            options.SerializerOptions.RespectNullableAnnotations = true;
            options.SerializerOptions.MaxDepth = 12;
        });
        return services;
    }
}
