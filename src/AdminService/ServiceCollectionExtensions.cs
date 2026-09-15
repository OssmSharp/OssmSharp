using System.Net;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Options;
using OSSMDotNet.DeviceControl;
using OSSMDotNet.Simulation;
using OSSMDotNet.Web.ControlCore;
using OSSMDotNet.Web.HostRuntime;

namespace OSSMDotNet.Web.AdminService;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAdminListener(this IServiceCollection services)
    {
        services.AddOptions<KestrelServerOptions>()
            .Configure<IOptions<AdminOptions>, IConfiguration>((server, options, configuration) =>
            {
                var port = options.Value.AdminPort;
                server.Configure(configuration.GetSection("Kestrel"), reloadOnChange: false);
                server.Listen(IPAddress.Loopback, port);
                server.Limits.MaxRequestBodySize = 16_384;
            });
        return services;
    }

    public static IServiceCollection AddAdminControl(this IServiceCollection services)
    {
        services.AddOperatorLog();
        services.AddSingleton<SimulatorProcessBridge>();
        services.AddSingleton<IDeviceBridge>(provider => provider.GetRequiredService<SimulatorProcessBridge>());
        services.AddSingleton<PrototypeEngine>();
        services.AddSingleton<AdminOperations>();
        return services;
    }

    public static IServiceCollection AddOperatorLog(this IServiceCollection services)
    {
        services.AddSingleton<OperatorLogProvider>();
        services.AddSingleton<ILoggerProvider>(provider => provider.GetRequiredService<OperatorLogProvider>());
        return services;
    }
}
