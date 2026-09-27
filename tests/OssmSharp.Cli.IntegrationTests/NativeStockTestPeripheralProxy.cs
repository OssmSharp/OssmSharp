#if WINDOWS || MACOS
using System.Reflection;
using Shiny.BluetoothLE;
#endif

namespace OssmSharp.Cli.IntegrationTests;

#if WINDOWS || MACOS
public class NativeStockTestPeripheralProxy : DispatchProxy
{
    private string _uuid = string.Empty;

    public static IPeripheral Create(string uuid)
    {
        var peripheral = DispatchProxy.Create<IPeripheral, NativeStockTestPeripheralProxy>();
        ((NativeStockTestPeripheralProxy)(object)peripheral)._uuid = uuid;
        return peripheral;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
        targetMethod?.Name == "get_Uuid"
            ? _uuid
            : throw new InvalidOperationException(
                $"The selected BLE identifier must not perform peripheral work ({targetMethod?.Name}).");
}
#endif
