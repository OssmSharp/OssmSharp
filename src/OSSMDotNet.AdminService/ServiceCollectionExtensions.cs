using System.Net;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Options;
using OSSMDotNet.DeviceBridge;
using OSSMDotNet.Models;
using OSSMDotNet.ControlCore;
using OSSMDotNet.ServiceDefaults;

namespace OSSMDotNet.AdminService;

/// <summary>Registers the admin listener, controller, and operator activity services.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>Registers the loopback-only admin listener.</summary>
    public static IServiceCollection AddAdminListener(this IServiceCollection services)
    {
        services.AddOptions<KestrelServerOptions>()
            .Configure<IOptions<AdminOptions>>((server, options) =>
            {
                var port = options.Value.AdminPort;
                server.Listen(IPAddress.Loopback, port);
                server.Limits.MaxRequestBodySize = 16_384;
            });
        return services;
    }

    /// <summary>Registers the admin control engine and device loop.</summary>
    public static IServiceCollection AddAdminControl(this IServiceCollection services)
    {
        services.AddOperatorLog();
        services.AddSingleton<SimulatorProcessBridge>();
        services.AddSingleton<IDeviceBridge>(provider => provider.GetRequiredService<SimulatorProcessBridge>());
        services.AddSingleton<PrototypeEngine>();
        services.AddSingleton<AdminOperations>();
        return services;
    }

    /// <summary>Registers the operator-visible activity log.</summary>
    public static IServiceCollection AddOperatorLog(this IServiceCollection services)
    {
        services.AddSingleton<OperatorLogProvider>();
        services.AddSingleton<ILoggerProvider>(provider => provider.GetRequiredService<OperatorLogProvider>());
        return services;
    }
}
