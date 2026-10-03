using Shiny.BluetoothLE;

namespace OssmSharp.Runtime.Oem.Protocol;

/// <summary>
/// Forwards Shiny peripheral lifecycle callbacks to their owning connections.
/// </summary>
internal sealed class BleLifecycleDelegate : IBleDelegate
{
    /// <summary>
    /// Ignores adapter access changes.
    /// </summary>
    /// <param name="state">The adapter's current bluetooth access state.</param>
    /// <returns>Completion without changing device state.</returns>
    public Task OnAdapterStateChanged(AccessState state) => Task.CompletedTask;

    /// <summary>
    /// Notifies the owning connection when a peripheral is disconnected.
    /// </summary>
    /// <param name="peripheral">The peripheral whose connection state changed.</param>
    /// <returns>Completion of the disconnection notification, if applicable.</returns>
    /// <remarks>
    /// Other connection states and peripherals without an owning <see cref="OssmDevice"/> are ignored.
    /// </remarks>
    public Task OnPeripheralStateChanged(IPeripheral peripheral)
    {
        OssmDevice.PeripheralStateChanged(peripheral);
        return Task.CompletedTask;
    }
}
