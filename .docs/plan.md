## External dependencies

- **Platform:** .NET 10 — runtime and standard libraries.
- **Bluetooth:** Shiny.BluetoothLE 5.7.2 — access Windows/macOS Bluetooth APIs.
- **Hardware:** OEM device and firmware — execute commands and own motor behavior and safety.
- **Simulator:** `ossm-rs` / `ossm-motion` and the simulator process — communicate with and test against the real simulator.
- **CLI parsing:** System.CommandLine 2.0.12 — parse commands and options, and generate help.
- **Hosting:** Microsoft.Extensions.Hosting 10.0.12 — manage executable composition and lifecycle.
- **Logging:** Microsoft.Extensions.Logging.Abstractions 10.0.12 — provide standard `ILogger<T>` diagnostics.
- **Test framework:** xUnit v3 — define and organize tests.
- **Test runner:** Microsoft.Testing.Platform v2 — discover and execute tests.
- **Test mocking:** FastMoq — construct test subjects with mocked dependencies.
- **Test assertions:** AwesomeAssertions — express readable assertions and failure messages.

## Project dependency order

- **`OssmSharp.Runtime.Oem`**
- **`OssmSharp.Runtime.Simulator`**
  - `OssmSharp.Runtime.Oem`
- **`OssmSharp.Cli`**
  - `OssmSharp.Runtime.Oem`
  - `OssmSharp.Runtime.Simulator`

## Projects, files, and functions

This is the concrete implementation contract. Every listed member, its ownership,
visibility, and specified behavior is required, not an implementation suggestion.

- **Consumer layer:** Runtime accepts complete motion settings and owns their translation, sequencing, supersession, state, and arming.
- **Command layer:** Runtime defines individual control assignments; OEM and Simulator encode and send one assignment per call.
- **Transport layer:** OEM uses Shiny Bluetooth; Simulator uses its protocol connection.

- **`OssmSharp.Runtime.Oem`**
  - **`DeviceControl.cs`**
    - Identify individual firmware controls, such as pattern, speed, stroke, depth, and sensation.
  - **`DeviceCommand.cs`**
    - Represent one assignment to one DeviceControl, such as setting speed to 20 percent.
  - **`IOssmDevice.cs`**
    - **ConnectAsync:** Connect to the selected device and subscribe to its state updates.
    - **SetAsync:** Submit a pattern identifier and control arguments through the device implementation.
    - **GetStateAsync(bypassCache):** Return cached state when bypassCache is false, or read live state when true, propagating read errors.
    - **EmergencyStopAsync:** Stop the device, clear pending requests, and disable further commands until explicit caller reset.
    - **ResetAsync:** Clear the disabled state without resubmitting previous motion.
    - **IsArmed (property):** Indicate whether command submission is enabled or disabled, pending caller reset.
    - **StateChanged (event):** Notify subscribers when the last known state or armed status changes.
    - **DisposeAsync:** Await owned operations and release the device connection and subscriptions.
  - **`MotionSettings.cs`**
    - Carry the pattern identifier and control arguments without interpreting pattern behavior.
    - Validate basic control ranges only, such as percentages between zero and 100.
    - **ToCommands (internal):** Expand these settings into an ordered sequence of individual DeviceCommand values without interpreting patterns or deciding motor safety.
  - **`DeviceState.cs`**
    - Represent the last known device state without inferring motion safety or imposing expiry.
  - **`DeviceBase.cs`** — abstract class implementing `IOssmDevice`.
    - **ExecuteAsync (private):** Serialize device I/O, awaiting WriteCommandAsync for each surviving primitive command or ReadStateAsync for a live read before proceeding.
    - **Supersede (private):** Replace pending settings with the latest submission and discard older unissued primitive commands without abandoning issued I/O.
    - **CancelPending (private):** Discard pending settings and unissued primitive commands without claiming that issued I/O was recalled.
    - **WriteCommandAsync(command) (protected abstract):** Send exactly one primitive command through the provider and complete when its issued I/O settles, propagating errors.
    - **ReadStateAsync (protected abstract):** Read live device state through the provider without maintaining another state cache.
    - **DisconnectAsync (protected abstract):** Release the provider connection and subscriptions after issued I/O settles.
    - **UpdateState (protected):** Store incoming state, detect external changes, and notify subscribers.
    - **DetectExternalChange (private):** Compare incoming controls with the current expectation; set IsArmed false and cancel pending work when external changes are unexplained.

  - **`DeviceDiscovery.cs`**
    - **ScanAsync:** Discover distinct devices advertising the fixed OSSM service within a bounded scan, returning identifiers and names and surfacing cancellation or Bluetooth errors.
  - **`DiscoveredDevice.cs`**
    - Carry the platform device identifier and advertised name for listing and connection selection.
  - **`OssmDevice.cs`** — extends `DeviceBase`; supplies Bluetooth operations while inheriting all settings execution from Runtime.
    - **WriteCommandAsync(command) (protected override):** Encode and write one DeviceCommand through Shiny Bluetooth without accepting or decomposing MotionSettings.
    - **ReadStateAsync (protected override):** Read firmware state over Bluetooth and decode it into DeviceState.
    - **DecodeState (private):** Decode firmware control values into DeviceState, surfacing malformed responses as errors.
    - **DisconnectAsync (protected override):** Close the owned Bluetooth connection and release subscriptions.
  - **`Request.cs`**
    - **Encode(command):** Encode one DeviceCommand into the firmware's wire format without interpreting pattern behavior.
  - **`ServiceCollectionExtensions.cs`**
    - **Register:** Register discovery and a factory accepting a discovered device identifier and returning DeviceBase.

- **`OssmSharp.Runtime.Simulator`**
  - **`SimulatedDevice.cs`** — extends `DeviceBase`; supplies simulator protocol operations while inheriting all settings execution from Runtime.
    - **WriteCommandAsync(command) (protected override):** Encode and send one DeviceCommand through the simulator protocol without accepting or decomposing MotionSettings.
    - **EncodeCommand(command) (private):** Encode one DeviceCommand into a simulator protocol request.
    - **ReadStateAsync (protected override):** Request live simulator state and decode it into DeviceState.
    - **DecodeState (private):** Decode simulator control values into DeviceState, surfacing malformed responses as errors.
    - **DisconnectAsync (protected override):** Close the connection without terminating the externally owned simulator process.
  - **`ServiceCollectionExtensions.cs`**
    - **Register:** Register the simulator implementation and its endpoint configuration.

- **`OssmSharp.Cli`**
  - **`Program.cs`**
    - **Main:** Parse list or prompt arguments, configure dependencies, invoke the selected operation, and return its exit code.
    - **ListAsync:** Discover devices and print their identifiers and names without connecting.
    - **PromptAsync:** Select the explicit identifier or sole discovered device, connect, run the prompt, and dispose the device on exit or failure.
  - **`PromptHandler.cs`**
    - **RunAsync:** Read commands while remaining responsive to newer settings and Stop.
    - **Set:** Parse positional settings into MotionSettings, submit through IOssmDevice, and report completion, supersession, validation errors, or partial failure with armed status.
    - **Stop:** Request emergency Stop and report its success or failure, pending-request cancellation, and armed status.
    - **Reset:** Explicitly reset the device and report its result and armed status without resubmitting previous motion.
    - **StatusAsync(bypassCache):** Display cached or live state as requested, together with whether command submission is armed.
    - **Help:** Describe available commands and basic argument ranges.
    - **Quit:** End command intake and await owned operations and cleanup.

## Consumer call chains (examples)

- **Connect and start the prompt:**
  - **Input:** `OssmSharp.Cli prompt`, optionally with `--device <id>`.
  - **Discovery:** `Program.Main` -> `BleServiceCollectionExtensions.Register` -> `Program.PromptAsync` -> `DeviceDiscovery.ScanAsync` -> Shiny Bluetooth scan.
  - **Connection:** `Program.PromptAsync` selects the explicit identifier or sole discovered device -> registered factory returns `DeviceBase` -> `IOssmDevice.ConnectAsync` (OEM implementation) -> Shiny Bluetooth connection.
  - **Outcome:** After connection succeeds, `Program.PromptAsync` passes the device as `IOssmDevice` to `PromptHandler.RunAsync`; ambiguous discovery exits without connecting.
  - **Cleanup:** On prompt exit or connection failure, `Program.PromptAsync` -> `IOssmDevice.DisposeAsync` -> `OssmDevice.DisconnectAsync`.
  - **Simulator path:** Use `ServiceCollectionExtensions.Register` and the same interface calls on `SimulatedDevice`; cleanup uses `SimulatedDevice.DisconnectAsync`.

- **Submit settings:**
  - **Input:** A connected device with `IOssmDevice.IsArmed` true and one complete `MotionSettings` value.
  - **Chain:** `PromptHandler.Set` -> `IOssmDevice.SetAsync` -> `DeviceBase.Supersede` -> `MotionSettings.ToCommands` -> `DeviceBase.ExecuteAsync` -> `DeviceBase.WriteCommandAsync(command)` (dispatches to `OssmDevice.WriteCommandAsync(command)`) -> `Request.Encode(command)` -> Shiny Bluetooth write -> device firmware.
  - **Example primitive:** The speed value in the submitted settings becomes one `DeviceCommand` assigning `DeviceControl.Speed`; other controls become separate commands.
  - **Execution:** `DeviceBase.ExecuteAsync` walks the primitive commands in order, checks for cancellation or supersession before each write, and calls the protected `WriteCommandAsync(command)` hook. That call invokes the selected provider's override; `ExecuteAsync` awaits its completion before advancing to the next command.
  - **Outcome:** Firmware receives the settings and determines motor behavior and safety; completing a write does not confirm physical motion.
  - **Asynchronous feedback:** Firmware notification -> `OssmDevice.DecodeState` -> `DeviceBase.UpdateState` -> `DeviceBase.DetectExternalChange` -> `IOssmDevice.StateChanged`.
  - **Simulator path:** The same Runtime chain calls `SimulatedDevice.WriteCommandAsync(command)` -> `SimulatedDevice.EncodeCommand(command)` -> simulator protocol connection.

- **Read cached or live state:**
  - **Input:** The consumer requests status with or without cache bypass.
  - **Cached chain:** `PromptHandler.StatusAsync(false)` -> `IOssmDevice.GetStateAsync(false)` -> last known state.
  - **Live chain:** `PromptHandler.StatusAsync(true)` -> `IOssmDevice.GetStateAsync(true)` -> `DeviceBase.ExecuteAsync` -> `OssmDevice.ReadStateAsync` -> Shiny Bluetooth read -> `OssmDevice.DecodeState` -> `DeviceBase.UpdateState`.
  - **Outcome:** Return the requested state and display it alongside `IOssmDevice.IsArmed`; cached reads require no I/O, and live read failures surface as errors.
  - **Simulator path:** Replace the Bluetooth read with `SimulatedDevice.ReadStateAsync` -> simulator protocol request -> `SimulatedDevice.DecodeState` -> `DeviceBase.UpdateState`.

- **Stop and disable sending:**
  - **Input:** The consumer requests Stop.
  - **Chain:** `PromptHandler.Stop` -> `IOssmDevice.EmergencyStopAsync` -> `DeviceBase.CancelPending` -> `DeviceBase.ExecuteAsync` (wait for issued I/O) -> `OssmDevice.WriteCommandAsync(command)` -> `Request.Encode(command)` -> Shiny Bluetooth write.
  - **Primitive:** Runtime sends a speed-zero DeviceCommand through the same single-command path, independently of other settings.
  - **Outcome:** Pending commands are cancelled and `IOssmDevice.IsArmed` is false until explicit caller reset; cancellation does not recall issued I/O.
  - **Simulator path:** Send the speed-zero primitive through `SimulatedDevice.WriteCommandAsync(command)` and the simulator protocol connection.

## Command-line prompt

### Find and connect

- `OssmSharp.Cli prompt` scans for the fixed OSSM Bluetooth service; users never enter service or characteristic identifiers.
- One matching device connects automatically after discovery completes; no selection question is shown.
- Multiple matching devices cause an error and exit without connecting; the message directs the user to `OssmSharp.Cli list`.
- `OssmSharp.Cli list` prints device identifiers and names without connecting; `OssmSharp.Cli prompt --device <id>` connects only to that selection.
- No matching device, an unavailable selected identifier, or a Bluetooth failure produces an actionable error and exits without opening the prompt.
- Connection does not arm or send motion settings; the user explicitly enters `reset` before sending settings.

### Prompt commands

- `set <pattern> <speed> <stroke> <depth> <sensation>` submits one complete settings value; all five arguments are required.
- Pattern is a nonnegative integer passed to firmware; each remaining argument is independently between 0 and 100 percent.
- `stop` sends emergency Stop, clears pending requests, and disarms command submission.
- `reset` enables command submission without sending previous settings.
- `status` displays cached state and armed status; `status --live` reads the device before displaying state.
- `help` displays commands, argument order, and ranges; `quit` exits after owned work and cleanup finish.

### Command results

- Each command identifies its outcome and current armed status; state output explicitly distinguishes cached values from live reads.
- A settings success means all primitive writes completed, not that physical motion was verified.
- Validate the entire input before submitting settings; report the offending argument, supplied value, expected range or syntax, and that nothing was sent.
- Superseded settings are reported as superseded, not successful; issued commands may already have taken effect.
- I/O failures identify the failed operation and any known partial progress; never claim rollback or replace failed live reads with cached success.
- Input errors keep the prompt open; a lost connection ends the session after cleanup and requires a new invocation.
- Send prompts, progress, and errors to stderr; send command results, help, and device listings to stdout.
- Process exit codes are `0` for normal completion, `1` for discovery, selection, connection, or terminal I/O failures, and `2` for invalid launch arguments.

### First command

The following identifiers and state values are illustrative.

```text
$ OssmSharp.Cli prompt
Searching for OSSM devices...
Found 1 device. Connecting to OSSM...
Connected. Armed: no. Enter reset to enable commands; help lists commands.
ossm> reset
OK reset: Armed: yes. No motion command sent.
ossm> set 0 20 50 50 25
OK set: all control commands sent.
ossm> status --live
OK status (live): pattern=0 speed=20 stroke=50 depth=50 sensation=25.
ossm> stop
OK stop: Stop command sent; pending requests cleared. Armed: no.
ossm> set 0 20 50 50 25
ERROR set: commands are disabled; enter reset before submitting settings. Nothing sent. Armed: no.
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
Connected. Armed: no. Enter reset to enable commands; help lists commands.
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
