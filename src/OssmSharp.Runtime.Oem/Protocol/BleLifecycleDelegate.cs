using Shiny.BluetoothLE;

namespace OssmSharp.Runtime.Oem.Protocol;

/// <summary>
/// Forwards Shiny peripheral lifecycle callbacks to their owning connections.
/// </summary>
internal sealed class BleLifecycleDelegate : IBleDelegate
{
    /// <inheritdoc />
    public Task OnAdapterStateChanged(AccessState state) => Task.CompletedTask;

    /// <inheritdoc />
    public Task OnPeripheralStateChanged(IPeripheral peripheral)
    {
        OssmDevice.PeripheralStateChanged(peripheral);
        return Task.CompletedTask;
    }
}
