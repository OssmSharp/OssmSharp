using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OssmSharp.Device.Primitives;

namespace OssmSharp.Cli.Interop;

internal static class CliServiceCollectionExtensions
{
    internal static IDisposable? AddCliPlatformServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

#if MACOS
        return services.AddMacOSPlatformServices();
#elif WINDOWS
        services.AddWindowsPlatformServices();
        return null;
#else
        services.TryAddSingleton<IDeviceDiscovery, UnavailableDeviceDiscovery>();
        services.TryAddSingleton<IDeviceProvider, UnavailableDeviceProvider>();
        return null;
#endif
    }
}
