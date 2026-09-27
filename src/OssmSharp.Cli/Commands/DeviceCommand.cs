namespace OssmSharp.Cli;

internal sealed class DeviceCommand : Command
{
    internal DeviceCommand(DeviceGetCommand get, DeviceListCommand list)
        : base("devices", "Discover and read stock BLE devices.")
    {
        Subcommands.Add(get);
        Subcommands.Add(list);
    }
}
