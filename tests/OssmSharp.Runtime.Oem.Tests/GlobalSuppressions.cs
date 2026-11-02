using System.Diagnostics.CodeAnalysis;

[assembly: SuppressMessage(
    "Maintainability",
    "CA1506",
    Justification = "This device lifecycle fixture integrates native Bluetooth mocks, protocol messages, reactive subscriptions, clocks, and runtime state. Splitting it only to reduce type counts would fragment shared lifecycle coverage.",
    Scope = "type",
    Target = "~T:OssmSharp.Runtime.Oem.Tests.OssmDeviceTests")]
