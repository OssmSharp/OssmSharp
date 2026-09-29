## External dependencies

- **Platform:** .NET 10 — runtime and standard libraries.
- **Bluetooth:** Shiny.BluetoothLE 5.7.2 — access Windows/macOS Bluetooth APIs.
- **Hardware:** Device and firmware - execute commands and own motor behavior and safety.
- **Wire protocol:** [RAD BLE v1](https://github.com/researchanddesire/rad-ble/blob/main/protocol/rad-ble-v1.json) - the supported Bluetooth protocol for discovery, requests, responses, control leases, and telemetry.
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
  - **Framework:** .NET 10.
  - **Package:** Shiny.BluetoothLE 5.7.2.
  - **Runtime connection:** Device firmware over RAD BLE v1.
- **`OssmSharp.Runtime.Simulator`**
  - **Project reference:** `OssmSharp.Runtime.Oem`.
  - **Runtime connection:** Externally owned simulator over its existing protocol.
- **`OssmSharp.Cli`**
  - **Project reference:** `OssmSharp.Runtime.Oem`.
  - **Project reference:** `OssmSharp.Runtime.Simulator`.

## Projects, files, and functions

This is the concrete implementation contract. Every listed member, its ownership,
visibility, and specified behavior is required, not an implementation suggestion.

- **Consumer layer:** Runtime accepts complete motion settings and owns their translation, sequencing, supersession, state, and arming.
- **Command layer:** Runtime defines individual control assignments; Bluetooth and Simulator providers encode and send one assignment per call.
- **Transport layer:** The Bluetooth provider owns RAD requests, responses, leases, and streams through Shiny; Simulator retains its protocol connection.

### Connection lifecycle

- A disconnected client remains reusable. Call `ConnectAsync` on the same instance to reconnect after an explicit disconnect, connection loss, or failed setup.
- `DeviceBase` serializes connection setup with device operations. Stop, disconnect, and disposal take precedence: each disarms, discards pending settings, abandons commands in flight, and immediately sends Stop over an established connection without waiting for other device I/O. Disconnect and disposal also abandon live reads, and release connection resources once that Stop settles, whether it succeeds or fails. Discarded or abandoned settings return Cancelled, not an exception; live reads without an established connection, ended connection attempts, and interrupted resets throw.
- Connection loss disarms and discards pending settings without sending anything.
- Before another connection attempt, finish cleanup of the previous connection, including partial setup. Cleanup failures propagate without opening another connection.
- Each connection creates fresh subscriptions and protocol state. Late observations, responses, and failures from an earlier connection must not affect the new connection.
- Successful connection or reconnection establishes fresh initial controls, resets expectations, clears the previous connection failure, and arms command submission. It never sends or replays motion settings.
- Stop, disconnect, or disposal during setup end that attempt; unexpected state during setup prevents it from arming. Failed or cancelled connection attempts remain disarmed.
- `DisconnectAsync` releases connection resources but preserves the client and consumer event subscriptions. `DisposeAsync` permanently ends the client lifetime and prevents reconnection.

### Types and members

- **`OssmSharp.Runtime.Oem`**
  - **`DeviceControl.cs`**
    - Identify individual firmware controls, such as pattern, speed, stroke, depth, and sensation.
  - **`DeviceCommand.cs`**
    - Represent one assignment to one DeviceControl, such as setting speed to 20 percent.
  - **`IOssmDevice.cs`**
    - **ConnectAsync:** Connect or reconnect the same undisposed client, initialize fresh state, and arm submission on success without sending or replaying motion settings. Throw when Stop, disconnect, or disposal ends the attempt.
    - **DisconnectAsync:** Disarm, discard pending settings, abandon commands and live reads in flight, send Stop immediately, then release connection resources once Stop settles while keeping the client reusable.
    - **SetAsync:** Submit complete MotionSettings and return SetResult with completion status and successful write count; write failures retain partial progress in DeviceWriteException. Submissions discarded or abandoned by Stop, disconnect, or disposal return Cancelled with the completed write count instead of throwing.
    - **GetStateAsync(bypassCache):** Return cached state, including while disconnected, or read live state when bypassCache is true, requiring a connection and propagating read errors. Live reads without an established connection, including those abandoned by disconnect or disposal, throw.
    - **EmergencyStopAsync:** Disarm, discard pending settings, abandon commands in flight, and send speed zero immediately without waiting for them or for a live read in progress, which continues; keep submission disarmed until explicit reset or successful reconnection, propagating Stop failures. During connection setup, end the attempt instead.
    - **ResetAsync:** Arm command submission without sending motion settings or clearing connection failures. Throw when Stop or disarming interrupts it.
    - **IsArmed (property):** Indicate whether command submission is armed by successful connection or explicit reset, or disarmed by Stop, unexpected state, or connection failure.
    - **ConnectionFailure (property):** Expose the current connection failure; clear it only after successful connection initialization, never through reset.
    - **StateChanged (event):** Notify subscribers when the last known state or armed status changes.
    - **DisposeAsync:** Disarm, discard pending settings, abandon commands and live reads in flight, send Stop immediately, then release connection resources and consumer subscriptions once Stop settles, and permanently prevent reconnection.
  - **`MotionSettings.cs`**
    - Carry a StrokePattern and control arguments without interpreting pattern behavior.
    - Require a defined StrokePattern and finite percentages between zero and 100; accept fractional percentages without pattern-specific validation.
    - **ToCommands (internal):** Expand these settings into an ordered sequence of individual DeviceCommand values without interpreting patterns or deciding motor safety.
  - **`StrokePattern.cs`** — public enum.
    - Identify the seven supported firmware patterns by name and ID (0..6), without implementing their behavior.
  - **`SetResult.cs`** — public record.
    - Carry SetStatus and CommandsWritten, the count of successfully completed primitive writes, without claiming rollback or physical motion confirmation.
  - **`SetStatus.cs`** — public enum.
    - Distinguish Completed, Superseded, and Cancelled submissions; commands already sent may have taken effect regardless of status.
  - **`DeviceState.cs`**
    - Represent complete last-observed controls without interpreting compact protocol metadata, inferring motion safety, or imposing expiry.
    - Preserve fractional percentages and require a defined pattern ID when decoding controls; reject malformed, missing, duplicate, or out-of-range values.
  - **`DeviceBase.cs`** — abstract class implementing `IOssmDevice`.
    - **ConnectCoreAsync (protected abstract):** Establish fresh provider connectivity and subscriptions; DeviceBase arms only after initial state initialization succeeds, without sending motion settings.
    - **ExecuteAsync (private):** Serialize device I/O other than Stop, awaiting WriteCommandAsync for each surviving primitive command or ReadStateAsync for a live read before proceeding.
    - **Supersede (private):** Replace pending settings with the latest submission and discard older primitive commands not yet sent, letting a command already sent finish.
    - **CancelPending (private):** Discard pending settings and primitive commands not yet sent, without claiming that a sent command was recalled.
    - **WriteCommandAsync(command) (protected abstract):** Send one primitive command, wait for the provider to confirm it finished or for Stop, disconnect, or disposal to abandon it, and propagate errors without claiming physical motion. Stop may call this while another command or live read is in progress.
    - **ReadStateAsync (protected abstract):** Read live device state through the provider without maintaining another state cache.
    - **DisconnectCoreAsync (protected abstract):** Release per-connection resources, including partial setup, without waiting for abandoned commands; tolerate repeated cleanup and preserve the provider instance for reconnection.
    - **UpdateState (protected):** Store incoming state, detect external changes, and notify subscribers.
    - **SetFaulted (protected):** Record failure of the current connection, disarm, and discard settings not yet sent, without sending commands or permanently preventing reconnection.
    - **DetectExternalChange (private):** Compare incoming controls with the current expectation; set IsArmed false and cancel pending work when external changes are unexplained.

  - **`DeviceDiscovery.cs`**
    - **ScanAsync:** Discover distinct devices advertising the fixed OSSM service within a bounded scan, returning identifiers and names and surfacing cancellation or Bluetooth errors.
  - **`DiscoveredDevice.cs`**
    - Carry the platform device identifier and advertised name for listing and connection selection.
  - **`BleProtocolConstants.cs`** — internal constants.
    - Define service `522b443a-4f53-534d-0001-420badbabe69` and characteristic suffixes: Protocol Info `0002`, Request `1000`, Response `1100`, State `2000`, Sensor Stream `2300`.
  - **`OssmDevice.cs`** — extends `DeviceBase`; supplies Bluetooth operations while inheriting all settings execution from Runtime.
    - **ConnectCoreAsync (protected override):** Open Bluetooth, validate RAD BLE v1, await fresh subscriptions, acquire a new control lease, and start streaming; leave arming to DeviceBase.
    - **WriteCommandAsync(command) (protected override):** Send one leased setting.write assignment and await its matching terminal response without accepting or decomposing MotionSettings.
    - **ReadStateAsync (protected override):** Await the next valid motion-stream observation and return complete DeviceState controls, propagating failures without returning cached success.
    - **SendRequestAsync (private):** Register a connection-scoped request ID, encode and send bounded JSON, await transport settlement and terminal response, and propagate failures.
    - **HandleResponse (private):** Validate response envelopes, correlate pending IDs, retain accepted requests, and resolve or fail them only at terminal stages.
    - **MaintainLeaseAsync (private):** Own acquisition and renewal using server-provided expiry, serialize protocol I/O, and fail the connection when control ownership is lost.
    - **StartMotionStreamAsync (private):** Start motion.speed streaming after subscription readiness and retain the returned stream identity for validating observations.
    - **HandleMotionObservation (private):** Validate stream frames, decode controls, satisfy pending live reads, and forward asynchronous observations through UpdateState without maintaining another controls cache.
    - **DecodeState (private):** Decode complete motion controls into DeviceState, rejecting malformed values without substituting defaults or treating compact State as controls.
    - **DisconnectCoreAsync (protected override):** Fail outstanding requests, settle renewal, stop streaming, release the lease when connected, and close per-connection resources without disposing the reusable client.
  - **`Request.cs`**
    - **Encode:** Serialize a RAD v1 request with nonzero ID, operation, optional path, arguments, and lease, preserving supplied values without interpreting patterns.
  - **`ServiceCollectionExtensions.cs`**
    - **Register:** Register discovery and a factory accepting a discovered device identifier and returning DeviceBase.

- **`OssmSharp.Runtime.Simulator`**
  - **`SimulatedDevice.cs`** — extends `DeviceBase`; supplies simulator protocol operations while inheriting all settings execution from Runtime.
    - **ConnectCoreAsync (protected override):** Open a fresh simulator connection and establish observations without starting its process or sending settings; leave arming to DeviceBase.
    - **WriteCommandAsync(command) (protected override):** Encode and send one DeviceCommand through the simulator protocol without accepting or decomposing MotionSettings.
    - **EncodeCommand(command) (private):** Encode one DeviceCommand into a simulator protocol request.
    - **ReadStateAsync (protected override):** Request live simulator state and decode it into DeviceState.
    - **DecodeState (private):** Decode simulator control values into DeviceState, surfacing malformed responses as errors.
    - **DisconnectCoreAsync (protected override):** Close per-connection resources without terminating the externally owned simulator process or preventing reconnection by this client.
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
  - **Reusable disconnect:** Library caller -> `IOssmDevice.DisconnectAsync` -> disarm, discard pending settings, and abandon work in flight -> `OssmDevice.WriteCommandAsync(command)` sends Stop immediately -> `OssmDevice.DisconnectCoreAsync`; the client remains reusable.
  - **Reconnect:** Library caller -> `IOssmDevice.ConnectAsync` on the same instance -> finish previous cleanup if needed -> `OssmDevice.ConnectCoreAsync` -> new lease and subscriptions -> fresh initial controls -> rearm submission without replaying settings.
  - **Cleanup:** On prompt exit or connection failure, `Program.PromptAsync` -> `IOssmDevice.DisposeAsync` -> disarm, discard pending settings, and abandon work in flight -> `OssmDevice.WriteCommandAsync(command)` sends Stop immediately while the connection and lease remain active -> `OssmDevice.DisconnectCoreAsync` -> stream stop, lease release when connected, and Bluetooth teardown; cleanup still runs if the disposal Stop fails, and disposal prevents reuse.
  - **Simulator path:** Use the same interface calls on `SimulatedDevice`, with `SimulatedDevice.ConnectCoreAsync` and `SimulatedDevice.DisconnectCoreAsync`; reconnection never restarts the simulator process.

- **Submit settings or Stop:**
  - **Input:** Settings require a connected, armed device and one complete `MotionSettings` value; Stop does not require arming.
  - **Chain:** `PromptHandler.Set` -> `IOssmDevice.SetAsync` -> `DeviceBase.Supersede` -> `MotionSettings.ToCommands` -> `DeviceBase.ExecuteAsync` -> `OssmDevice.WriteCommandAsync(command)` -> `OssmDevice.SendRequestAsync` -> `Request.Encode` -> Shiny Request write -> firmware `setting.write`.
  - **Example primitive:** The speed value in the submitted settings becomes one `DeviceCommand` assigning `DeviceControl.Speed`; other controls become separate commands.
  - **Execution:** `DeviceBase.ExecuteAsync` walks the primitive commands in order, checks for cancellation or supersession before each write, and calls the protected `WriteCommandAsync(command)` hook. That call invokes the selected provider's override; `ExecuteAsync` awaits its completion before advancing to the next command.
  - **Completion:** Response indication -> `OssmDevice.HandleResponse` -> matching ID reaches `completed` or `failed` -> pending `OssmDevice.SendRequestAsync` settles; GATT acknowledgement and `accepted` are insufficient.
  - **Outcome:** Every assignment must complete successfully; firmware owns motor behavior and safety, and protocol completion does not confirm physical motion.
  - **Stop:** `PromptHandler.Stop` or `PromptHandler.Quit` -> `IOssmDevice.EmergencyStopAsync` -> `DeviceBase.CancelPending` -> `OssmDevice.WriteCommandAsync(command)` sends speed zero immediately through the same leased request path, without waiting for a setting write or live read already in progress.
  - **Stop outcome:** The firmware handles requests in the order received, so a command sent just before Stop may briefly take effect. Submission stays disarmed until explicit reset or successful reconnection; Stop failure is surfaced, and neither cancellation nor lease release proves the motor stopped.
  - **Simulator path:** The same Runtime chain calls `SimulatedDevice.WriteCommandAsync(command)` -> `SimulatedDevice.EncodeCommand(command)` -> simulator protocol connection.

- **Read cached or live state:**
  - **Input:** The consumer requests status with or without cache bypass.
  - **Cached chain:** `PromptHandler.StatusAsync(false)` -> `IOssmDevice.GetStateAsync(false)` -> last known state.
  - **Live chain:** `PromptHandler.StatusAsync(true)` -> `IOssmDevice.GetStateAsync(true)` -> `DeviceBase.ExecuteAsync` -> `OssmDevice.ReadStateAsync` -> subsequent Sensor Stream frame -> `OssmDevice.HandleMotionObservation` -> `OssmDevice.DecodeState` -> `DeviceBase.UpdateState`.
  - **Asynchronous feedback:** Sensor Stream frame -> `OssmDevice.HandleMotionObservation` -> `OssmDevice.DecodeState` -> `DeviceBase.UpdateState` -> `DeviceBase.DetectExternalChange` -> `IOssmDevice.StateChanged`, including while no commands are pending.
  - **Failure:** Invalid controls, lost stream, or lost lease -> `DeviceBase.SetFaulted`; compact State alone neither replaces controls nor constitutes malformed motion telemetry.
  - **Outcome:** Return the requested state and display it alongside `IOssmDevice.IsArmed`; cached reads require no I/O, and live read failures surface as errors.
  - **Simulator path:** Replace the Bluetooth read with `SimulatedDevice.ReadStateAsync` -> simulator protocol request -> `SimulatedDevice.DecodeState` -> `DeviceBase.UpdateState`.

## Command-line prompt

### Find and connect

- `OssmSharp.Cli prompt` scans for the fixed OSSM Bluetooth service; users never enter service or characteristic identifiers.
- One matching device connects automatically after discovery completes; no selection question is shown.
- Multiple matching devices cause an error and exit without connecting; the message directs the user to `OssmSharp.Cli list`.
- `OssmSharp.Cli list` prints device identifiers and names without connecting; `OssmSharp.Cli prompt --device <id>` connects only to that selection.
- No matching device, an unavailable selected identifier, or a Bluetooth failure produces an actionable error and exits without opening the prompt.
- Successful connection arms command submission without sending or replaying motion settings; `reset` is needed only to rearm a connected client after disarming.

### Prompt commands

- `set <pattern> <speed> <stroke> <depth> <sensation>` submits one complete settings value; all five arguments are required.
  - Pattern is a `StrokePattern` ID (0..6) or case-insensitive name; each remaining argument is independently a finite percentage between 0 and 100, allowing fractions.
- `stop` sends emergency Stop immediately, discards queued commands, and disarms command submission.
- `reset` enables command submission without sending previous settings.
- `status` displays cached state and armed status; `status --live` reads the device before displaying state.
- `help` lists commands; `help <command>` displays command-specific help, with argument order, pattern choices, and percentage ranges available through `help set`.
- `quit` sends Stop, waits for it to finish, then exits after cleanup.

### Command results

- Each command identifies its outcome and current armed status; state output explicitly distinguishes cached values from live reads.
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
ossm> status --live
OK status (live): pattern=0 speed=20 stroke=50 depth=50 sensation=25.
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

## Appendix

An optional `RadBleConnection` transport adapter could isolate Bluetooth lifecycle, RAD requests, responses, leases, and subscriptions from `OssmDevice`.
