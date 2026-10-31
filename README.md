# OssmSharp

A .NET library for discovering and controlling OSSM devices over Bluetooth.
Send a pattern and settings; the device firmware controls motion and safety.

Looking for the browser app? Read the [web app guide](README.web.md).

## Install

```sh
dotnet add package OssmSharp.Runtime.Oem
```

The package includes the shared runtime contracts. No separate
`OssmSharp.Runtime` package is needed.

Use .NET 10 with a native target:

| Platform | Target framework | Requirements |
| --- | --- | --- |
| Windows | `net10.0-windows10.0.19041.0` | Windows 10 version 2004 or later, or Windows 11 |
| macOS | `net10.0-macos27.0` | macOS 26 or later; macOS workload and Xcode 27 to build |

The portable `net10.0` target supports shared code and tests, not native Bluetooth.
Enable Bluetooth and grant the app Bluetooth access.

## Quick start

Create a Windows console app:

```sh
dotnet new console -n OssmExample
cd OssmExample
dotnet add package OssmSharp.Runtime.Oem
```

Set `TargetFramework` in `OssmExample.csproj` to
`net10.0-windows10.0.19041.0`, then replace `Program.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using OssmSharp.Runtime;
using OssmSharp.Runtime.Oem;

var services = new ServiceCollection();
services.AddOssmSharpOemRuntime();
using var provider = services.BuildServiceProvider();

var discovery = provider.GetRequiredService<IDeviceDiscovery>();
var devices = await discovery.ScanAsync();
if (devices.Count != 1)
{
    throw new InvalidOperationException(
        $"Expected one OSSM device; found {devices.Count}. Select a device explicitly.");
}

var createDevice = provider.GetRequiredService<Func<string, IOssmDevice>>();
await using var device = createDevice(devices[0].Identifier);
await device.ConnectAsync();

var result = await device.SetAsync(new MotionSettings(
    StrokePattern.SimpleStroke, speed: 0, stroke: 50, depth: 50, sensation: 50));
Console.WriteLine($"Settings: {result.Status}");

var state = await device.GetStateAsync(bypassCache: true);
var speed = state?.Speed is { } value ? $"{value}%" : "unknown";
Console.WriteLine($"Current speed: {speed}");
await device.EmergencyStopAsync();
```

Run with `dotnet run`. The example requests zero speed. A nonzero `speed` requests
motion. If several devices are found, select the intended device by its `Identifier`
instead of choosing the first.

For macOS, declare `NSBluetoothAlwaysUsageDescription` and
`NSBluetoothPeripheralUsageDescription` in the app's `Info.plist`. Console apps
also need a background Bluetooth dispatch queue; see
[HostRunner.cs](src/OssmSharp.Cli/HostRunner.cs) and
[Info.plist](src/OssmSharp.Cli/Info.plist).

## Settings and state

`MotionSettings` takes a defined `StrokePattern` and four integer percentages
from `0` to `100`. Use `50` for 50%, not `0.5`. The library checks ranges, not
pattern-specific rules.

Speed is a percentage, not physical speed or strokes per minute. Its conversion
depends on stroke, not depth or sensation. The library sends
`floor(speed * stroke / 100)` as raw velocity. Zero speed, zero stroke, or a
result below 1% sends zero.

`DeviceState.Speed` uses velocity and stroke from the same device snapshot.
It preserves decimals and values above 100%, without rounding or capping.
At zero stroke it is `null` (unknown). For example, 53% requested speed at
10% stroke sends 5% raw velocity, giving 50% current speed.
Keep these conversions in the library; consumers should use `Speed` directly.

`GetStateAsync()` returns the last known state, including while disconnected.
Use `bypassCache: true` for a fresh observation. Subscribe to `StateChanged` for
state or arming changes; check `ConnectionFailure` for connection errors.

## Connection and stopping

Each device instance owns its connection exclusively. The caller must dispose it
asynchronously. Connecting arms requests without sending earlier settings.
New settings replace older commands not yet sent.

### Stop or emergency stop

| Action | API | Behavior |
| --- | --- | --- |
| Normal stop | `SetAsync()` with `MotionSettings.Speed` set to `0` | Uses the normal settings path; requires a connected, armed, ready device. |
| Emergency stop | `EmergencyStopAsync()` | Drops pending settings and sends zero speed ahead of queued work. Works while disarmed. Success preserves arming; failure disarms. |

Emergency stop waits for a Bluetooth transfer already started, but not an earlier
response or live read. Neither stop prevents later settings from requesting motion.

### Armed or disarmed

Arming is a library permission, not a physical device state.

| State or action | Meaning | What your application must do |
| --- | --- | --- |
| Armed (`IsArmed == true`) | Settings requests are allowed, subject to connection and readiness checks. It does not mean the device is moving. | Call `SetAsync()` to send settings. |
| Disarmed (`IsArmed == false`) | Settings requests are blocked. Emergency stop remains available. | Require explicit user confirmation that the device is ready before rearming; do not automatically retry motion settings. |
| Rearm | `ResetAsync()` enables requests on a connected device without sending or restoring settings. | After confirmation, await `ResetAsync()`, then send new settings explicitly. Reconnect first after a connection failure. |

This confirmation step reduces unwanted restarts after an error; it is not a
hardware safety guarantee. A successful emergency stop does **not** disarm or
require rearming. `ResetPositionAsync()` is different: it moves the rail back
and stops, rather than granting permission to send settings.

`DisconnectAsync()` stops and closes the connection but allows reuse.
After a connection failure, call `ConnectAsync()` to reconnect. Disposal is final.
Errors propagate; `SetResult.Status` distinguishes completed, superseded, and
cancelled requests.

## More

- [Architecture](docs/ARCHITECTURE.md)
- [Build and release](docs/RELEASING.md)
- [CLI example](src/OssmSharp.Cli)
- [License](LICENSE): AGPL-3.0-only; dependencies retain their own licenses.
