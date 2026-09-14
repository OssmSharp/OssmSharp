using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Text.Json.Serialization;

namespace OSSMDotNet.Web.HostRuntime;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAdminConfiguration(this IServiceCollection services)
    {
        services.AddOptions<AdminOptions>()
            .BindConfiguration("OSSM", options => options.ErrorOnUnknownConfiguration = true)
            .ValidateDataAnnotations()
            .ValidateOnStart();
        return services;
    }

    public static IServiceCollection AddGuestConfiguration(this IServiceCollection services)
    {
        services.AddOptions<GuestOptions>()
            .BindConfiguration("OSSM", options => options.ErrorOnUnknownConfiguration = true)
            .ValidateDataAnnotations()
            .ValidateOnStart();
        return services;
    }

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
