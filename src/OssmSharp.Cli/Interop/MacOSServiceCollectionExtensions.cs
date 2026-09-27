#if MACOS
using CoreFoundation;
using Microsoft.Extensions.DependencyInjection;
using OssmSharp.Device.Stock;

namespace OssmSharp.Cli.Interop;

internal static class MacOSServiceCollectionExtensions
{
    internal static IDisposable AddMacOSPlatformServices(this IServiceCollection services)
    {
        var dispatchQueue = new DispatchQueue("org.ossm.devicecli");
        IDisposable lifetime = dispatchQueue;

        try
        {
            services.AddStockBluetooth(dispatchQueue);
            return lifetime;
        }
        catch
        {
            lifetime.Dispose();
            throw;
        }
    }
}
#endif
