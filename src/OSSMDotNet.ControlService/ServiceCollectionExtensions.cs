using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OSSMDotNet.ControlCore;
using OSSMDotNet.ControlService.Activity;
using OSSMDotNet.ControlService.Authority;
using OSSMDotNet.ControlService.Operations;
using OSSMDotNet.ControlService.Models;

namespace OSSMDotNet.ControlService;

/// <summary>Registers the transport-neutral control application.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the application authority over the composition root's selected device.
    /// </summary>
    /// <param name="services">The service collection receiving the control services.</param>
    /// <returns>The same service collection for further registration.</returns>
    public static IServiceCollection AddControlService(this IServiceCollection services)
    {
        services.AddControlActivity();
        services.AddSingleton<PrototypeEngine>();
        services.AddSingleton<ControlAuthority>();
        services.AddSingleton<GuestControlOperations>();
        services.AddSingleton<IControlOperations, ControlOperations>();
        return services;
    }

    /// <summary>
    /// Registers bounded operator-visible activity logging.
    /// </summary>
    /// <param name="services">The service collection receiving the activity provider.</param>
    /// <returns>The same service collection for further registration.</returns>
    public static IServiceCollection AddControlActivity(this IServiceCollection services)
    {
        services.AddSingleton<ControlActivityProvider>();
        services.AddSingleton<ILoggerProvider>(provider => provider.GetRequiredService<ControlActivityProvider>());
        return services;
    }

}
