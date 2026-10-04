## External dependencies

- **Platform:** .NET 10 - runtime and standard libraries.
- **Bluetooth:** Shiny.BluetoothLE 5.7.2 - access Windows and macOS Bluetooth APIs.
- **Hardware:** Device and firmware - execute commands and own motor behavior and safety.
- **Wire protocol:** [RAD BLE v1](https://github.com/researchanddesire/rad-ble/blob/main/protocol/rad-ble-v1.json) - discovery, requests, responses, control leases, and telemetry.
- **CLI parsing:** System.CommandLine 2.0.12 - parse commands and options, and generate help.
- **Interactive input:** PrettyPrompt 6.0.5 - read prompt commands with history.
- **Hosting:** Microsoft.Extensions.Hosting 10.0.12 - manage executable composition and lifecycle.
- **Options:** Microsoft.Extensions.Options 10.0.12 - supply request retry and response timeout limits.
- **Logging:** Microsoft.Extensions.Logging.Abstractions 10.0.12 - provide `ILogger<T>` diagnostics.
- **Testing:** xUnit v3 on Microsoft.Testing.Platform v2, FastMoq, and AwesomeAssertions.

## Project dependency order

- **`OssmSharp.Cli`** - consumer layer.
  - **Framework:** .NET 10
  - **Packages:** System.CommandLine 2.0.12, PrettyPrompt 6.0.5, Microsoft.Extensions.Hosting 10.0.12
  - **Project reference:** `OssmSharp.Runtime.Oem` - runtime and transport layers.
    - **Framework:** .NET 10
    - **Packages:** Shiny.BluetoothLE 5.7.2, Microsoft.Extensions.Options 10.0.12
    - **Runtime connection:** Device firmware over RAD BLE v1
    - **Project reference:** `OssmSharp.Runtime` - shared result and discovery contracts.
      - **Framework:** .NET 10
      - **Package:** Microsoft.Extensions.Logging.Abstractions 10.0.12

## Projects, files, and functions

- **Consumer layer:** callers submit complete motion settings; the latest request wins. A regular stop is a request with speed zero; an emergency stop uses `EmergencyStopAsync`.
- **Runtime layer:** `OssmDevice` decomposes settings into commands and coordinates supersession, readiness, arming, and last known state.
- **Transport layer:** `OssmDevice` private members and `Request` encode and execute individual RAD requests through Shiny.

- **`OssmSharp.Cli`**
  - **`Program.cs`**
    - **Main:** Parse list or prompt arguments, configure dependencies, run the selected command, and return its exit code.
    - **ListAsync:** Discover devices and print their identifiers and names without connecting.
    - **PromptAsync:** Connect to the explicit or sole discovered device, run the prompt, and dispose the device on exit or failure.
  - **`PromptHandler.cs`**
    - **RunAsync:** Read and execute prompt commands until quit or end of input.
    - **Set:** Parse five positional arguments into MotionSettings, submit them, and report the result with armed status.
    - **Stop:** Request emergency Stop and report its outcome with armed status.
    - **Reset:** Rearm the device without resubmitting settings and report the result.
    - **StatusAsync(bypassCache):** Display cached or live state with armed status.
    - **Help (private):** Display general or command-specific help with argument ranges and armed status.
    - **Quit:** Stop the device and exit after Stop and cleanup finish.

- **`OssmSharp.Runtime`**
  - **`IDeviceDiscovery.cs`**
    - **ScanAsync:** Return distinct devices advertising the OSSM service within a bounded scan, propagating cancellation and Bluetooth errors.
    - **FindFirstAsync:** Return the first device advertising the OSSM service, or null when the bounded scan finds none.
  - **`DiscoveredDevice.cs`** - public record carrying a platform device identifier and advertised name.
  - **`SetResult.cs`** - public record carrying SetStatus and the number of commands the firmware completed.
  - **`SetStatus.cs`** - public enum distinguishing Completed, Superseded, and Cancelled requests.
  - **`DeviceWriteException.cs`** - public exception reporting a failed request and the commands completed before it failed.

- **`OssmSharp.Runtime.Oem`**
  - **`IOssmDevice.cs`**
    - **ConnectAsync:** Connect or reconnect, read initial state, and arm without sending motion settings.
    - **DisconnectAsync:** Disarm, abandon pending work, send or join Stop, and release the connection while keeping the client reusable.
    - **SetAsync:** Reject without sending or changing arming when disconnected, disarmed, not ready, or emergency stopping; otherwise return once all commands complete or another call supersedes.
    - **GetStateAsync(bypassCache):** Return last known state, or the next live observation when bypassing the cache.
    - **EmergencyStopAsync:** Cancel pending settings and write speed zero, rejecting Set and Reset until complete; success preserves arming and failure disarms.
    - **ResetAsync:** Arm without resending settings or clearing connection failures; reject during an emergency stop or when disarmed after the call.
    - **IsArmed (property):** Indicate whether settings may be sent.
    - **ConnectionFailure (property):** Expose the current connection failure until a successful reconnection.
    - **StateChanged (event):** Notify subscribers when last known state or armed status changes.
    - **DisposeAsync:** Disarm, send or join Stop, release resources, and prohibit reuse.
  - **`MotionSettings.cs`** - public class.
    - **Constructor:** Require a defined StrokePattern and whole-number percentages from 0 to 100, without pattern-specific validation.
    - **ToCommands (internal):** Expand the settings into one command per control, ending with speed.
  - **`StrokePattern.cs`** - public enum identifying the seven firmware patterns by name and ID 0 to 6.
  - **`DeviceState.cs`** - public record.
    - **FromBytes:** Decode one observation's controls, preserving fractional speed and rejecting malformed, missing, duplicate, or out-of-range values.
  - **`DeviceDiscovery.cs`** - implements `IDeviceDiscovery` through Shiny.
  - **`OssmDeviceOptions.cs`** - public class carrying request attempt and response timeout limits, validated at startup by `OssmDeviceOptionsValidator`.
  - **`OssmDevice.cs`** - public sealed class implementing `IOssmDevice`.
    - **DisconnectCoreAsync (private):** Stop streaming, release the lease, and close the Bluetooth connection.
    - **MaintainLeaseAsync (private):** Acquire and renew the control lease, failing the connection when ownership is lost.
    - **StartMotionStreamAsync (private):** Start the motion observation stream.
    - **ReadStateAsync (private):** Return the next valid motion observation.
    - **HandleMotionObservation (private):** Validate and decode stream frames into last known state and pending live reads.
    - **WriteCommandAsync (private):** Send one control assignment and await the firmware's terminal response.
    - **SendRequestAsync (private):** Send one RAD request and return its terminal response.
    - **HandleResponse (private):** Match a response to its pending request and complete it at a terminal stage.
  - **`Request.cs`** - internal.
    - **Encode:** Serialize one RAD v1 request without interpreting its arguments.
  - **`ServiceCollectionExtensions.cs`**
    - **AddOssmSharpOemRuntime:** Register discovery, validated options, and an `IOssmDevice` factory, requiring an explicit `IBleManager` outside Windows and macOS.

## Consumer call chains (examples)

- **Connect:**
  - **Input:** `OssmSharp.Cli prompt`
  - **Chain:** `Program.PromptAsync` -> `IDeviceDiscovery.ScanAsync` -> factory from `AddOssmSharpOemRuntime` -> `IOssmDevice.ConnectAsync` (`OssmDevice` implementation) -> `OssmDevice.MaintainLeaseAsync` -> `OssmDevice.StartMotionStreamAsync` -> `OssmDevice.ReadStateAsync`.
  - **Return:** Initial state recorded -> device armed -> `PromptHandler.RunAsync` starts.

- **Submit settings:**
  - **Input:** `set 0 20 50 50 25`
  - **Chain:** `PromptHandler.Set` -> `IOssmDevice.SetAsync` (`OssmDevice` implementation) -> `MotionSettings.ToCommands` -> `OssmDevice.WriteCommandAsync` per command -> `OssmDevice.SendRequestAsync` -> `Request.Encode` -> RAD `setting.write`.
  - **Return:** `OssmDevice.HandleResponse` completes each request -> `SetResult` Completed, or Superseded when a newer call replaces it.

- **Read live state:**
  - **Input:** `status`
  - **Chain:** `PromptHandler.StatusAsync(true)` -> `IOssmDevice.GetStateAsync(true)` (`OssmDevice` implementation) -> `OssmDevice.ReadStateAsync` -> `OssmDevice.HandleMotionObservation` -> `DeviceState.FromBytes`.
  - **Return:** Decoded state -> CLI displays it with armed status.

## Command-line prompt

### Find and connect

- `OssmSharp.Cli prompt` scans for the fixed OSSM Bluetooth service; users never enter service or characteristic identifiers.
- One matching device connects automatically after discovery completes; no selection question is shown.
- Multiple matching devices cause an error and exit without connecting; the message directs the user to `OssmSharp.Cli list`.
- `OssmSharp.Cli list` prints device identifiers and names without connecting; `OssmSharp.Cli prompt --device <id>` connects only to that selection.
- No matching device, an unavailable selected identifier, or a Bluetooth failure produces an actionable error and exits without opening the prompt.
- Successful connection arms command request without sending or replaying motion settings; `reset` is needed only to rearm a connected client after disarming.

### Prompt commands

- `set <pattern> <speed> <stroke> <depth> <sensation>` asks the device to run patterns, then submits one complete settings value; all five arguments are required.
  - Pattern is a `StrokePattern` ID (0..6) or case-insensitive name; each remaining argument is independently a whole-number percentage between 0 and 100. Decimal and exponent forms are rejected.
  - When the device is homing or waiting for its speed knob to reach zero, no settings are sent and the prompt stays armed.
- `stop` sends emergency Stop immediately and discards pending settings. Success preserves current arming, so another `set` does not require `reset`; failure disarms.
- `reset` enables command request without sending previous settings.
- `status` reads the device's live state and displays it with armed status, preserving fractional effective speed.
- `help` lists commands; `help <command>` displays command-specific help, with argument order, pattern choices, and percentage ranges available through `help set`.
- `quit` sends Stop, waits for it to finish, then exits after cleanup.
- Commands execute one at a time. The next command is not read until the current request and terminal responses complete; physical motion can continue until stopped.
- The prompt persists command history in the current user's application-data directory. Recalled commands require explicit request.
- Ctrl+C while an operation is pending cancels it and issues Stop; at other times it starts normal exit and cleanup.

### Command results

- Each command identifies its outcome and current armed status on its own line. Results and background diagnostics clear and redraw the active prompt and partial input without blocking new commands. Long input scrolls within one terminal row.
- Only outcome keywords are colored: green for success, yellow for superseded or cancelled work, and red for errors. Redirected streams, `NO_COLOR`, and `TERM=dumb` suppress outcome colors.
- A settings success means all primitive writes completed, not that physical motion was verified.
- Validate the entire input before submitting settings; report the offending argument, supplied value, expected range or syntax, and that nothing was sent.
- Superseded settings are reported as superseded, not successful; commands already sent may have taken effect.
- I/O failures identify the failed operation and any known partial progress; never claim rollback or replace failed live reads with cached success.
- Input errors keep the prompt open; a lost connection ends this CLI session after cleanup and requires a new invocation. Library callers may instead reconnect an undisposed client.
- Send prompts, progress, and errors to stderr; send command results, help, and device listings to stdout.
- Process exit codes are `0` for normal completion, `1` for discovery, selection, connection, or terminal I/O failures, and `2` for invalid launch arguments.

### First command

The following identifiers and state values are illustrative.

```text
$ OssmSharp.Cli prompt
Searching for OSSM devices...
Found 1 device. Connecting to OSSM...
Connected. Armed: yes. No motion command sent; help lists commands.
ossm> set 0 20 50 50 25
OK set: all control commands sent.
ossm> status
OK status: pattern=0 speed=20 stroke=50 depth=50 sensation=25.
ossm> stop
OK stop: Stop command sent; pending requests cleared. Armed: no.
ossm> set 0 20 50 50 25
ERROR set: commands are disarmed; enter reset before submitting settings. Nothing sent. Armed: no.
```

### Multiple devices

```text
$ OssmSharp.Cli prompt
Searching for OSSM devices...
ERROR: Found 2 OSSM devices. No connection made.
Run OssmSharp.Cli list, then OssmSharp.Cli prompt --device <id>.

$ OssmSharp.Cli list
device-a  OSSM
device-b  OSSM

$ OssmSharp.Cli prompt --device device-b
Connecting to OSSM (device-b)...
Connected. Armed: yes. No motion command sent; help lists commands.
```

### Validation and write failures

```text
ossm> set 0 120 50 50 25
ERROR set: speed '120' must be between 0 and 100. Nothing sent. Armed: yes.
ossm> set 0 20 50
ERROR set: missing depth and sensation; expected set <pattern> <speed> <stroke> <depth> <sensation>. Nothing sent. Armed: yes.
ossm> set 0 20 50 50 25
ERROR set: Bluetooth connection lost during a control write; settings may be partially applied. Device state is unknown. Commands unavailable.
Session ended. Run OssmSharp.Cli prompt to reconnect.
```

## Appendix: Future Ideas

These ideas are deferred and are not part of the current implementation contract.

### Observation-triggered Auto-disarm

Consider disarming request and discarding unissued settings after an unexplained control change.
Control values alone cannot identify who changed them. This requires distinguishing external changes
from delayed feedback on this client's writes and firmware mode-entry resets, without treating
knob-scaled effective speed as an external change. The current design exposes observations through
`StateChanged` and `GetStateAsync` without inferring their source or changing arming.

### RAD Transport Adapter

An optional `RadBleConnection` transport adapter could isolate Bluetooth lifecycle, RAD requests, responses, leases, and subscriptions from `OssmDevice`.

### Source-generated JSON

Parsing uses `JsonDocument` with manual checks, and encoding uses `Utf8JsonWriter`; both are already trim- and AOT-safe.
A `JsonSerializerContext` with `required` members, `AllowDuplicateProperties = false`, and `JsonUnmappedMemberHandling`
could replace the manual helpers. Checks for `stage`, `v == 1`, and `protocol == "rad-ble"` would remain.
