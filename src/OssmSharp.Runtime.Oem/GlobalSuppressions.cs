using System.Diagnostics.CodeAnalysis;

[assembly: SuppressMessage(
    "Usage",
    "CA2213",
    Justification = "Stop and lease writes can outlive failed cleanup. Disposing would race their waits and releases. AvailableWaitHandle is never accessed, so no native handle is allocated.",
    Scope = "member",
    Target = "~F:OssmSharp.Runtime.Oem.OssmDevice.writes")]
[assembly: SuppressMessage(
    "Usage",
    "CA2213",
    Justification = "ResetPositionAsync owns and disposes this source after joining Stop, including when device disposal interrupts it.",
    Scope = "member",
    Target = "~F:OssmSharp.Runtime.Oem.OssmDevice.resetPosition")]
[assembly: SuppressMessage(
    "Performance",
    "CA1849",
    Justification = "Shiny's async wrappers add a timeout that abandons issued GATT I/O; the raw observables must be awaited to settlement.",
    Scope = "member",
    Target = "~M:OssmSharp.Runtime.Oem.OssmDevice.ConnectCoreAsync(System.Threading.CancellationToken)~System.Threading.Tasks.Task")]
[assembly: SuppressMessage(
    "Performance",
    "CA1849",
    Justification = "Shiny's async wrapper adds a timeout that abandons issued GATT I/O; the raw observable must be awaited to settlement.",
    Scope = "member",
    Target = "~M:OssmSharp.Runtime.Oem.OssmDevice.WriteRequestAsync(System.Threading.CancellationTokenSource,Shiny.BluetoothLE.IPeripheral,System.Byte[],System.Threading.Tasks.TaskCompletionSource,System.Action,System.Threading.CancellationToken)~System.Threading.Tasks.Task")]
