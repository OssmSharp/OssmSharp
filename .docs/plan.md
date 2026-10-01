## External dependencies

- **Platform:** .NET 10 — runtime and standard libraries.
- **Bluetooth:** Shiny.BluetoothLE 5.7.2 — access Windows/macOS Bluetooth APIs.
- **Hardware:** Device and firmware - execute commands and own motor behavior and safety.
- **Wire protocol:** [RAD BLE v1](https://github.com/researchanddesire/rad-ble/blob/main/protocol/rad-ble-v1.json) - the supported Bluetooth protocol for discovery, requests, responses, control leases, and telemetry.
- **CLI parsing:** System.CommandLine 2.0.12 — parse commands and options, and generate help.
- **Hosting:** Microsoft.Extensions.Hosting 10.0.12 — manage executable composition and lifecycle.
- **Logging:** Microsoft.Extensions.Logging.Abstractions 10.0.12 — provide standard `ILogger<T>` diagnostics.
- **Test framework:** xUnit v3 — define and organize tests.
- **Test runner:** Microsoft.Testing.Platform v2 — discover and execute tests.
- **Test mocking:** FastMoq — construct test subjects with mocked dependencies.
- **Test assertions:** AwesomeAssertions — express readable assertions and failure messages.

## Project dependency order

- **`OssmSharp.Runtime.Oem`**
  - **Framework:** .NET 10.
  - **Package:** Shiny.BluetoothLE 5.7.2.
  - **Runtime connection:** Device firmware over RAD BLE v1.
- **`OssmSharp.Cli`**
  - **Project reference:** `OssmSharp.Runtime.Oem`.

## Projects, files, and functions

This is the concrete implementation contract. Every listed member, its ownership,
visibility, and specified behavior is required, not an implementation suggestion.

- **Consumer layer:** Runtime accepts complete motion settings and owns their translation, sequencing, supersession, state, and arming.
- **Command layer:** Runtime defines individual control assignments; the Bluetooth provider encodes and sends one assignment per call.
- **Transport layer:** The Bluetooth provider owns RAD requests, responses, leases, and streams through Shiny.

### Types and members

- **`OssmSharp.Runtime.Oem`**
  - **`DeviceControl.cs`**
    - Identify individual firmware controls, such as pattern, speed, stroke, depth, and sensation.
  - **`DeviceCommand.cs`**
    - Represent one assignment to one DeviceControl, such as setting speed to 20 percent.
  - **`IOssmDevice.cs`**
    - **ConnectAsync:** Connect or reconnect the same undisposed client, initialize fresh state, and arm submission on success without sending or replaying motion settings. Throw when Stop, disconnect, or disposal ends the attempt.
    - **DisconnectAsync:** Disarm and abandon settings and live reads; send or join Stop when connected, then clean up even if it fails while preserving reuse.
    - **SetAsync:** Submit MotionSettings and count confirmed writes; return Cancelled on Stop, disconnect, or disposal, and retain genuine failures' partial progress in DeviceWriteException.
    - **GetStateAsync(bypassCache):** Return cached state, including while disconnected, or read live state when bypassCache is true, requiring a connection and propagating read errors. Live reads without an established connection, including those abandoned by disconnect or disposal, throw.
    - **EmergencyStopAsync:** Abandon pending settings and send or join Stop without waiting for earlier responses or live reads, or end an unfinished connection attempt. Success preserves current arming; failure disarms.
    - **ResetAsync:** Arm without resending settings or clearing failures; throw when disarmed later or while Stop is in progress.
    - **IsArmed (property):** Indicate submission arming: successful connection or explicit reset arms; successful Stop preserves current arming; Stop failure, operational failure, disconnect, and disposal disarm.
    - **ConnectionFailure (property):** Expose the current connection failure; clear it only after successful connection initialization, never through reset.
    - **StateChanged (event):** Notify subscribers when the last known state or armed status changes.
    - **DisposeAsync:** Disarm and abandon settings and live reads; send or join Stop when connected, then release resources even on failure and prohibit reuse.
  - **`MotionSettings.cs`**
    - Carry a StrokePattern and control arguments without interpreting pattern behavior.
    - Require a defined StrokePattern and whole-number percentages between zero and 100, without pattern-specific validation.
    - **ToCommands (internal):** Expand these settings into an ordered sequence of individual DeviceCommand values without interpreting patterns or deciding motor safety.
  - **`StrokePattern.cs`** — public enum.
    - Identify the seven supported firmware patterns by name and ID (0..6), without implementing their behavior.
  - **`SetResult.cs`** — public record.
    - Carry SetStatus and CommandsWritten, the count of successfully completed primitive writes, without claiming rollback or physical motion confirmation.
  - **`SetStatus.cs`** — public enum.
    - Distinguish Completed, Superseded, and Cancelled submissions; commands already sent may have taken effect regardless of status.
  - **`DeviceState.cs`**
    - Represent complete last-observed controls without interpreting compact protocol metadata, inferring motion safety, or imposing expiry.
    - Expose observed Pattern, Speed, Stroke, Depth, and Sensation directly. Preserve fractional effective speed; require integer values for the other controls and reject malformed, missing, duplicate, or out-of-range values.
  - **`DeviceBase.cs`** — abstract class implementing `IOssmDevice`.

    - **ConnectCoreAsync (protected abstract):** Establish fresh provider connectivity and subscriptions; DeviceBase arms only after initial state initialization succeeds, without sending motion settings.
    - **ExecuteAsync (private):** Serialize non-Stop I/O, abandon cancelled operation waits, and prevent queued settings from running after disarm.
    - **Supersede (private):** Replace pending settings with the latest submission and discard older primitive commands not yet sent, letting a command already sent finish.
    - **CancelPending (private):** Discard pending settings and not-yet-issued writes without claiming an issued write was recalled.
    - **WriteCommandAsync(command) (protected abstract):** Send one control assignment while owning issued transport writes after abandonment; Stop awaits terminal completion without claiming physical motion.
    - **ReadStateAsync (protected abstract):** Read live device state through the provider without maintaining another state cache.
    - **DisconnectCoreAsync (protected abstract):** Release resources after Stop and issued transport writes settle, without waiting for abandoned responses; tolerate partial setup and reconnection.
    - **UpdateState (protected):** Store incoming last known state and notify subscribers without changing arming or cancelling pending submissions.
    - **SetFaulted (protected):** Record failure of the current connection, disarm, and discard settings not yet sent, without sending commands or permanently preventing reconnection.

  - **`DeviceDiscovery.cs`**
    - **ScanAsync:** Discover distinct devices advertising the fixed OSSM service within a bounded scan, returning identifiers and names and surfacing cancellation or Bluetooth errors.
  - **`DiscoveredDevice.cs`**
    - Carry the platform device identifier and advertised name for listing and connection selection.
  - **`BleProtocolConstants.cs`** — internal constants.
    - Define service `522b443a-4f53-534d-0001-420badbabe69` and characteristic suffixes: Protocol Info `0002`, Request `1000`, Response `1100`, State `2000`, Sensor Stream `2300`.
  - **`OssmDevice.cs`** — extends `DeviceBase`; supplies Bluetooth operations while inheriting all settings execution from Runtime.
    - **ConnectCoreAsync (protected override):** Open Bluetooth, validate RAD BLE v1, await fresh subscriptions, acquire a new control lease, and start streaming; leave arming to DeviceBase.
    - **WriteCommandAsync(command) (protected override):** Send one leased setting.write assignment; abandon an earlier setting's terminal-response wait, not its issued GATT transfer.
    - **ReadStateAsync (protected override):** Await the next valid motion-stream observation and return complete DeviceState controls, propagating failures without returning cached success.
    - **SendRequestAsync (private):** Register and encode bounded requests, recheck queued writes at the GATT gate, send Stop next, and own issued transfers until settlement.
    - **HandleResponse (private):** Validate response envelopes, correlate pending IDs, retain accepted requests, and resolve or fail them only at terminal stages.
    - **MaintainLeaseAsync (private):** Own acquisition and renewal using server-provided expiry, serialize protocol I/O, and fail the connection when control ownership is lost.
    - **StartMotionStreamAsync (private):** Start motion.speed streaming after subscription readiness and retain the returned stream identity for validating observations.
    - **HandleMotionObservation (private):** Validate stream frames, decode controls, satisfy pending live reads, and forward asynchronous observations through UpdateState without maintaining another controls cache.
    - **DecodeState (private):** Decode complete motion controls into DeviceState, rejecting malformed values without substituting defaults or treating compact State as controls.
    - **DisconnectCoreAsync (protected override):** Fail abandoned response waits, settle transfers and renewal, stop streaming, release the lease, and close per-connection resources without disposing the reusable client.
  - **`Request.cs`**
    - **Encode:** Serialize a RAD v1 request with nonzero ID, operation, optional path, arguments, and lease, preserving supplied values without interpreting patterns.
  - **`ServiceCollectionExtensions.cs`**
    - **Register:** Register discovery and a factory accepting a discovered device identifier and returning DeviceBase.

- **`OssmSharp.Cli`**
  - **`Program.cs`**
    - **Main:** Parse list or prompt arguments, configure dependencies, invoke the selected operation, and return its exit code.
    - **ListAsync:** Discover devices and print their identifiers and names without connecting.
    - **PromptAsync:** Select the explicit identifier or sole discovered device, connect, run the prompt, and dispose the device on exit or failure.
  - **`PromptHandler.cs`**
    - **RunAsync:** Read commands while remaining responsive to newer settings and Stop.
    - **Set:** Parse positional settings into MotionSettings, submit through IOssmDevice, and report completion, supersession, validation errors, or partial failure with armed status.
    - **Stop:** Request emergency Stop and report its success or failure, discarded queued commands, and armed status.
    - **Reset:** Explicitly reset the device and report its result and armed status without resubmitting previous motion.
    - **StatusAsync(bypassCache):** Display cached or live state as requested, together with whether command submission is armed.
    - **Help (private):** Display general or command-specific parser help, including argument ranges for set, and current armed status.
    - **Quit:** Stop the device, end command intake, and wait for the Stop and cleanup to finish before exiting. End of input behaves the same.

## Consumer call chains (examples)

- **Connect and start the prompt:**
  - **Input:** `OssmSharp.Cli prompt`, optionally with `--device <id>`.
  - **Discovery:** `Program.Main` -> Bluetooth `ServiceCollectionExtensions.Register` -> `Program.PromptAsync` -> `DeviceDiscovery.ScanAsync` -> Shiny Bluetooth scan.
  - **Connection:** `Program.PromptAsync` selects the explicit identifier or sole discovered device -> registered factory returns `DeviceBase` -> `IOssmDevice.ConnectAsync` -> `OssmDevice.ConnectCoreAsync` -> Shiny connection, Protocol Info validation, and awaited subscriptions.
  - **Initialization:** `OssmDevice.MaintainLeaseAsync` -> `OssmDevice.SendRequestAsync` -> `Request.Encode` -> `control.acquire`; `OssmDevice.StartMotionStreamAsync` -> the same request path -> `stream.start` on `motion.speed`.
  - **Initial controls:** `IOssmDevice.ConnectAsync` -> `OssmDevice.ReadStateAsync` -> next valid motion observation -> `OssmDevice.DecodeState` -> `DeviceBase.UpdateState`; successful initialization arms submission without sending motion settings.
  - **Outcome:** After connection succeeds, `Program.PromptAsync` passes the device as `IOssmDevice` to `PromptHandler.RunAsync`; ambiguous discovery exits without connecting.
  - **Reusable disconnect:** Library caller -> `IOssmDevice.DisconnectAsync` -> disarm and abandon pending work -> `OssmDevice.WriteCommandAsync(command)` sends or joins Stop after the current GATT send -> `OssmDevice.DisconnectCoreAsync`; the client remains reusable.
  - **Reconnect:** Library caller -> `IOssmDevice.ConnectAsync` on the same instance -> finish previous cleanup if needed -> `OssmDevice.ConnectCoreAsync` -> new lease and subscriptions -> fresh initial controls -> rearm submission without replaying settings.
  - **Cleanup:** On prompt exit or connection failure, `Program.PromptAsync` -> `IOssmDevice.DisposeAsync` -> disarm and abandon pending work -> `OssmDevice.WriteCommandAsync(command)` sends or joins Stop while connected and leased -> `OssmDevice.DisconnectCoreAsync` -> stream stop, lease release, and Bluetooth teardown; cleanup still runs if Stop fails, and disposal prevents reuse.

- **Submit settings or Stop:**
  - **Input:** Settings require a connected, armed device and one complete `MotionSettings` value; Stop does not require arming.
  - **Chain:** `PromptHandler.Set` -> `IOssmDevice.SetAsync` -> `DeviceBase.Supersede` -> `MotionSettings.ToCommands` -> `DeviceBase.ExecuteAsync` -> `OssmDevice.WriteCommandAsync(command)` -> `OssmDevice.SendRequestAsync` -> `Request.Encode` -> Shiny Request write -> firmware `setting.write`.
  - **Example primitive:** The speed value in the submitted settings becomes one `DeviceCommand` assigning `DeviceControl.Speed`; other controls become separate commands.
  - **Execution:** `DeviceBase.ExecuteAsync` walks the primitive commands in order, checks for cancellation or supersession before each write, and calls the protected `WriteCommandAsync(command)` hook. That call invokes the selected provider's override; `ExecuteAsync` awaits its completion before advancing to the next command.
  - **Completion:** Response indication -> `OssmDevice.HandleResponse` -> matching ID reaches `completed` or `failed` -> pending `OssmDevice.SendRequestAsync` settles; GATT acknowledgement and `accepted` are insufficient.
  - **Outcome:** Every assignment must complete successfully; firmware owns motor behavior and safety, and protocol completion does not confirm physical motion.
  - **Stop:** `PromptHandler.Stop` or `PromptHandler.Quit` -> `IOssmDevice.EmergencyStopAsync` -> `DeviceBase.CancelPending` -> `OssmDevice.WriteCommandAsync(command)` -> `OssmDevice.SendRequestAsync` sends speed zero next after the current GATT write, without waiting for its terminal response or a live read.
  - **Stop outcome:** The firmware handles requests in the order received, so a command sent just before Stop may briefly take effect; no queued setting may follow Stop. Success preserves prior arming, failure disarms, overlapping callers share the Stop outcome, and neither cancellation nor lease release proves the motor stopped.

- **Read cached or live state:**
  - **Input:** The consumer requests status with or without cache bypass.
  - **Cached chain:** `PromptHandler.StatusAsync(false)` -> `IOssmDevice.GetStateAsync(false)` -> last known state.
  - **Live chain:** `PromptHandler.StatusAsync(true)` -> `IOssmDevice.GetStateAsync(true)` -> `DeviceBase.ExecuteAsync` -> `OssmDevice.ReadStateAsync` -> subsequent Sensor Stream frame -> `OssmDevice.HandleMotionObservation` -> `OssmDevice.DecodeState` -> `DeviceBase.UpdateState`.
  - **Asynchronous feedback:** Sensor Stream frame -> `OssmDevice.HandleMotionObservation` -> `OssmDevice.DecodeState` -> `DeviceBase.UpdateState` -> `IOssmDevice.StateChanged`, including while no commands are pending.
  - **Observation policy:** Changed controls update last known state only; they do not disarm submission, discard queued commands, or trigger Stop.
  - **Failure:** Invalid controls, lost stream, or lost lease -> `DeviceBase.SetFaulted`; compact State alone neither replaces controls nor constitutes malformed motion telemetry.
  - **Outcome:** Return the requested state and display it alongside `IOssmDevice.IsArmed`; cached reads require no I/O, and live read failures surface as errors.

## Command-line prompt

### Find and connect

- `OssmSharp.Cli prompt` scans for the fixed OSSM Bluetooth service; users never enter service or characteristic identifiers.
- One matching device connects automatically after discovery completes; no selection question is shown.
- Multiple matching devices cause an error and exit without connecting; the message directs the user to `OssmSharp.Cli list`.
- `OssmSharp.Cli list` prints device identifiers and names without connecting; `OssmSharp.Cli prompt --device <id>` connects only to that selection.
- No matching device, an unavailable selected identifier, or a Bluetooth failure produces an actionable error and exits without opening the prompt.
- Successful connection arms command submission without sending or replaying motion settings; `reset` is needed only to rearm a connected client after disarming.

### Prompt commands

- `set <pattern> <speed> <stroke> <depth> <sensation>` asks the device to run patterns, then submits one complete settings value; all five arguments are required.
  - Pattern is a `StrokePattern` ID (0..6) or case-insensitive name; each remaining argument is independently a whole-number percentage between 0 and 100. Decimal and exponent forms are rejected.
  - When the device is homing or waiting for its speed knob to reach zero, no settings are sent and the prompt stays armed.
- `stop` sends emergency Stop immediately and discards pending settings. Success preserves current arming, so another `set` does not require `reset`; failure disarms.
- `reset` enables command submission without sending previous settings.
- `status` reads the device's live state and displays it with armed status, preserving fractional effective speed.
- `help` lists commands; `help <command>` displays command-specific help, with argument order, pattern choices, and percentage ranges available through `help set`.
- `quit` sends Stop, waits for it to finish, then exits after cleanup.
- Commands execute one at a time. The next command is not read until the current submission and terminal responses complete; physical motion can continue until stopped.
- The prompt persists command history in the current user's application-data directory. Recalled commands require explicit submission.
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

Consider disarming submission and discarding unissued settings after an unexplained control change.
Control values alone cannot identify who changed them. This requires distinguishing external changes
from delayed feedback on this client's writes and firmware mode-entry resets, without treating
knob-scaled effective speed as an external change. The current design exposes observations through
`StateChanged` and `GetStateAsync` without inferring their source or changing arming.

### RAD Transport Adapter

An optional `RadBleConnection` transport adapter could isolate Bluetooth lifecycle, RAD requests, responses, leases, and subscriptions from `OssmDevice`.
