#if MACOS
using CoreFoundation;
using Microsoft.Extensions.DependencyInjection;
using OssmSharp.Device.Stock;

namespace OssmSharp.Cli.Interop;

internal static class MacOSServiceCollectionExtensions
{
    internal static IDisposable AddMacOSPlatformServices(this IServiceCollection services)
    {
#pragma warning disable XCODE_27_0_PREVIEW
        var dispatchQueue = new DispatchQueue("org.ossm.devicecli");
#pragma warning restore XCODE_27_0_PREVIEW
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
