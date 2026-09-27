#if WINDOWS || MACOS
using Shiny.BluetoothLE;

namespace OssmSharp.Device.Stock;

/// <summary>An immutable selection tied to the manager that discovered its peripheral.</summary>
internal sealed record StockDeviceIdentifier : DeviceIdentifier
{
    private readonly IPeripheral _peripheral;
    private readonly IBleManager _owner;

    internal StockDeviceIdentifier(string id, string name, IPeripheral peripheral, IBleManager owner)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        Id = id;
        Name = name ?? throw new ArgumentNullException(nameof(name));
        _peripheral = peripheral ?? throw new ArgumentNullException(nameof(peripheral));
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
    }

    public override string Id { get; }

    public override string Name { get; }

    /// <summary>Rejects foreign, stale or changed selections before opening native I/O.</summary>
    internal static IPeripheral Validate(DeviceIdentifier identifier, IBleManager manager)
    {
        ArgumentNullException.ThrowIfNull(identifier);
        ArgumentNullException.ThrowIfNull(manager);
        if (identifier is not StockDeviceIdentifier selected
            || !ReferenceEquals(selected._owner, manager)
            || string.IsNullOrWhiteSpace(selected.Id)
            || !string.Equals(selected.Id, selected._peripheral.Uuid, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The selected device does not belong to this stock BLE composition.", nameof(identifier));
        }

        return selected._peripheral;
    }
}
#endif
