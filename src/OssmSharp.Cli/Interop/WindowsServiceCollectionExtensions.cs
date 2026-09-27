#if WINDOWS
using Microsoft.Extensions.DependencyInjection;
using OssmSharp.Device.Stock;

namespace OssmSharp.Cli.Interop;

internal static class WindowsServiceCollectionExtensions
{
    internal static void AddWindowsPlatformServices(this IServiceCollection services) =>
        services.AddStockBluetooth();
}
#endif
