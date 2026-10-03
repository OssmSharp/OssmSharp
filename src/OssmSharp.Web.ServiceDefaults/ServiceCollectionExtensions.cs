using System.Net;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.HostFiltering;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using OssmSharp.Web.ServiceDefaults.Models;

namespace OssmSharp.Web.ServiceDefaults;

/// <summary>
/// Registers validated local host configuration and shared Blazor hosting services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers and validates the loopback-only admin configuration at startup.
    /// </summary>
    /// <param name="services">The host service collection.</param>
    /// <returns>The supplied service collection.</returns>
    public static IServiceCollection AddAdminConfiguration(this IServiceCollection services)
    {
        services.AddOptions<AdminOptions>()
            .BindConfiguration("OSSM:Admin", options => options.ErrorOnUnknownConfiguration = true)
            .ValidateDataAnnotations()
            .Validate<IConfiguration>(
                (_, configuration) =>
                    !configuration.GetSection("Kestrel").Exists()
                    && !configuration.GetValue<bool>(WebHostDefaults.PreferHostingUrlsKey),
                "Admin listener overrides are not supported. Use OSSM:Admin:AdminPort for the 127.0.0.1 listener.")
            .ValidateOnStart();
        return services;
    }

    /// <summary>
    /// Registers and validates the guest listener configuration at startup.
    /// </summary>
    /// <param name="services">The host service collection.</param>
    /// <returns>The supplied service collection.</returns>
    public static IServiceCollection AddGuestConfiguration(this IServiceCollection services)
    {
        services.AddOptions<GuestOptions>()
            .BindConfiguration("OSSM:Guest", options => options.ErrorOnUnknownConfiguration = true)
            .ValidateDataAnnotations()
            .Validate<IConfiguration>(
                (_, configuration) =>
                    !configuration.GetSection("Kestrel").Exists()
                    && !configuration.GetValue<bool>(WebHostDefaults.PreferHostingUrlsKey),
                "Guest listener overrides are not supported. Use OSSM:Guest:GuestPort for the 127.0.0.1 listener.")
            .ValidateOnStart();
        services.AddOptions<HostFilteringOptions>().Configure<IOptions<GuestOptions>>((filter, options) =>
        {
            filter.AllowedHosts = options.Value.Hostname.Length == 0
                ? ["127.0.0.1"]
                : ["127.0.0.1", new Uri(options.Value.Hostname).Host];
        });
        return services;
    }

    /// <summary>
    /// Registers the fixed loopback listener for the unauthenticated admin web host.
    /// </summary>
    /// <param name="services">The host service collection.</param>
    /// <returns>The supplied service collection.</returns>
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

    /// <summary>
    /// Registers constrained interactive Blazor defaults.
    /// </summary>
    /// <param name="services">The host service collection.</param>
    /// <returns>The supplied service collection.</returns>
    public static IServiceCollection AddLocalBlazor(this IServiceCollection services)
    {
        services.AddProblemDetails();
        services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = false);
        services.TryAddSingleton(TimeProvider.System);
        services.AddDataProtection();
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
