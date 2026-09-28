using Shiny.BluetoothLE;

namespace OssmSharp.Device.Oem.ShinyInterop;

/// <summary>
/// Forwards Shiny peripheral lifecycle callbacks to their owning OEM connections.
/// </summary>
internal sealed class BleLifecycleDelegate : IBleDelegate
{
    /// <summary>
    /// Ignores adapter access changes.
    /// </summary>
    /// <param name="state">The adapter's current Bluetooth access state.</param>
    /// <returns>Completion without changing device state.</returns>
    public Task OnAdapterStateChanged(AccessState state) => Task.CompletedTask;

    /// <summary>
    /// Notifies the owning OEM connection when a peripheral is disconnected.
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
