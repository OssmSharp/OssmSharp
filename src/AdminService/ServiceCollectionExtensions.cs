using OSSMDotNet.DeviceControl;
using OSSMDotNet.Simulation;
using OSSMDotNet.Web.ControlCore;

namespace OSSMDotNet.Web.AdminService;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAdminControl(this IServiceCollection services)
    {
        services.AddOperatorLog();
        services.AddSingleton<OperatorCapability>();
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
