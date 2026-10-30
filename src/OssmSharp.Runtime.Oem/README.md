# OssmSharp device library

Discover and control OSSM devices over Bluetooth from .NET 10. The package
includes the shared runtime contracts; no separate runtime package is needed.

## Install

```sh
dotnet add package OssmSharp.Runtime.Oem
```

## Requirements

Choose a native target:

- `net10.0-windows10.0.19041.0`: Windows 10 version 2004 or later, or Windows 11.
- `net10.0-macos27.0`: macOS 26 or later, with the .NET macOS workload and Xcode 27
  when building.

The portable `net10.0` target is for tests and shared code, not native Bluetooth.

## Use

Register `services.AddOssmSharpOemRuntime()`, scan with `IDeviceDiscovery`, and
create a device with the registered `Func<string, IOssmDevice>`. Use the device
to connect, send `MotionSettings`, read state, and stop. Dispose it asynchronously.
It has exclusive control while connected; the firmware controls motion and safety.

Settings use integer percentages from 0 to 100. Current `DeviceState.Speed`
preserves decimals and can exceed 100%; it is unknown at zero stroke.

See the [quick start](https://github.com/OssmSharp/OssmSharp#quick-start) for a
complete example and macOS Bluetooth setup.

## License and source

AGPL-3.0-only. The package includes the license and exact source commit metadata.
[Source](https://github.com/OssmSharp/OssmSharp). Dependencies retain their licenses.
