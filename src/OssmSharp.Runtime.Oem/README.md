# OssmSharp device library

The `OssmSharp.Runtime.Oem` NuGet package provides Bluetooth discovery and control
for OSSM devices. It includes both the device library and its shared runtime
contracts. No separate `OssmSharp.Runtime` package is required.

## Requirements

Use .NET 10 and one of these native targets:

- `net10.0-windows10.0.19041.0`: Windows 10 version 2004 or later, or Windows 11.
- `net10.0-macos27.0`: macOS 26 or later, with the .NET macOS workload and Xcode 27
  when building.

Use the portable `net10.0` target for tests and shared code. Bluetooth requires
one of the native targets above.

```xml
<PackageReference Include="OssmSharp.Runtime.Oem" Version="0.1.0" />
```

## Use

Register services with `services.AddOssmSharpOemRuntime()`. Use `IDeviceDiscovery`
to discover devices and `IOssmDevice` to connect, set motion settings, and stop.
Dispose the device when finished. It has exclusive control while connected.

The registration configures Bluetooth services automatically. macOS applications
must still declare Bluetooth usage descriptions in `Info.plist`.

## License and source

OssmSharp is licensed under AGPL-3.0-only. The package includes the full license
text. Its repository metadata identifies the exact source commit.

Source and application examples: <https://github.com/argium/OssmSharp>.
Third-party dependencies retain their own licenses.
