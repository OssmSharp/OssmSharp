#if WINDOWS
using Shiny.BluetoothLE;
#endif

namespace OssmSharp.Cli;

internal sealed class DeviceCommand : Command
{
#if WINDOWS
    internal DeviceCommand(IBleManager manager)
        : this(new DeviceGetCommand(Console.Out, Console.Error), new DeviceListCommand(manager))
    {
    }
#else
    internal DeviceCommand()
        : this(
            new DeviceGetCommand(Console.Out, Console.Error),
            new DeviceListCommand(
                Device.Runtime.DeviceOwnershipLock.TryAcquire,
                _ => throw new NotSupportedException("Stock BLE discovery requires a supported native build."),
                Console.Out, Console.Error))
            {
    }
#endif

    internal DeviceCommand(DeviceGetCommand get, DeviceListCommand list)
        : base("device", "Discover stock BLE devices.")
    {
        Subcommands.Add(get);
        Subcommands.Add(list);
    }
}
