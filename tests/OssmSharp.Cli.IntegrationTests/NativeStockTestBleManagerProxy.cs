#if WINDOWS || MACOS
using System.Reflection;
using Shiny.BluetoothLE;
#endif

namespace OssmSharp.Cli.IntegrationTests;

#if WINDOWS || MACOS
public class NativeStockTestBleManagerProxy : DispatchProxy
{
    public static IBleManager Create() =>
        DispatchProxy.Create<IBleManager, NativeStockTestBleManagerProxy>();

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
        throw new InvalidOperationException(
            $"The substituted BLE manager must not perform native work ({targetMethod?.Name}).");
}
#endif
