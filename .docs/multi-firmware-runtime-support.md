# Hypothetical firmware-specific runtime codemap

Status: proposed contract for discussion, not approval to implement or replace
[the current plan](plan.md). If adopted, every listed member, owner, visibility,
and behavior is required. `SomethingElse` remains a placeholder, not a project
to implement before its firmware contract is selected.

## External dependencies

- **Platform:** .NET 10 - runtime and standard libraries.
- **Bluetooth:** Shiny.BluetoothLE 5.7.2 - access Windows/macOS Bluetooth APIs.
- **Hardware:** OEM device and firmware - execute patterns and own motor behavior and safety.
- **CLI parsing:** System.CommandLine 2.0.12 - parse commands and generate help.
- **Hosting:** Microsoft.Extensions.Hosting 10.0.12 - compose the CLI and own its lifecycle.
- **Logging:** Microsoft.Extensions.Logging.Abstractions 10.0.12 - provide operational diagnostics through `ILogger<T>`.
- **Test framework:** xunit.v3.mtp-v2 3.2.2 - define tests using xUnit v3.
- **Test runner:** Microsoft.Testing.Platform 2.4.0 - discover and execute tests.
- **Test mocking:** FastMoq.Core 4.5.0 - construct subjects with mocked dependencies.
- **Test assertions:** AwesomeAssertions 9.6.0 - express observable behavior assertions.

## Project dependency order

- **`OssmSharp.Runtime`**
  - **Framework:** .NET 10.
  - **Package:** Microsoft.Extensions.Logging.Abstractions 10.0.12.
  - **Execution layer:** Coordinate primitive operations, supersession, arming, cancellation, and lifecycle through one execution owner.
  - **Boundary:** No firmware types, pattern interpretation, state cache, transport dependencies, or five-control assumptions.
- **`OssmSharp.Runtime.Oem`**
  - **Framework:** .NET 10.
  - **Project reference:** `OssmSharp.Runtime`.
  - **Package:** Shiny.BluetoothLE 5.7.2.
  - **OEM runtime layer:** Accept complete OEM settings, decompose them into primitive assignments, and interpret OEM observations.
  - **Primitive layer:** Own OEM controls, commands, pattern identifiers, settings, and decoded state.
  - **Transport layer:** Encode and execute individual OEM assignments through Bluetooth without decomposing settings in the write path.
  - **Runtime connection:** OEM firmware over Bluetooth; the firmware executes patterns and decides motor behavior and safety.
- **`OssmSharp.Runtime.SomethingElse`** - hypothetical sibling only.
  - **Framework:** .NET 10.
  - **Project reference:** `OssmSharp.Runtime`, never `OssmSharp.Runtime.Oem`.
  - **Firmware ownership:** Supply distinct typed requests, primitives, observations, and transport operations without extending OEM enums.
  - **Runtime connection:** Undecided until a concrete firmware and protocol are selected.
- **`OssmSharp.Cli`**
  - **Framework:** .NET 10.
  - **Package:** System.CommandLine 2.0.12.
  - **Package:** Microsoft.Extensions.Hosting 10.0.12.
  - **Project reference:** `OssmSharp.Runtime` for lifecycle-only consumers.
  - **Project reference:** `OssmSharp.Runtime.Oem` for typed OEM settings and state.
  - **Consumer layer:** Submit complete OEM requests and display results without constructing primitive writes.

The separate Primitives project disappears: its types belong to the OEM
contract. Firmware packages never reference each other. Transport implementations
remain in their firmware package unless independent packaging becomes necessary.
Shared lifecycle does not make pattern identities or settings interchangeable.

## Projects, files, and functions

- **`OssmSharp.Runtime`**
  - **`IOssmDevice.cs`** - extends `IAsyncDisposable`; shared lifecycle contract only.
    - **ConnectAsync:** Establish the connection and initial observation without arming or sending motion settings.
    - **EmergencyStopAsync:** Discard pending submissions and execute firmware Stop after issued I/O settles. Success preserves the current arming state; failure disarms.
    - **ResetAsync:** Explicitly arm without restoring settings, rejecting resets interrupted by concurrent Stop or disarming.
    - **IsArmed (property):** Indicate whether explicit caller reset has enabled submission.
    - **StateChanged (event):** Notify consumers when arming or firmware-owned last known state changes.
    - **DisposeAsync (inherited):** Disarm, cancel unissued work, await issued operations, and release subscriptions and the connection, including after partial connection failure.
  - **`DeviceBase.cs`** - abstract class implementing `IOssmDevice`; non-generic execution owner.
    - **SubmitAsync (protected):** Supersede older submissions and execute finite ordered command operations, checking cancellation and arming before issuance and awaiting each issued operation.
    - **ExecuteAsync (protected):** Serialize live reads and lifecycle I/O with writes, retain issued-work ownership, and disarm on operational failure.
    - **Supersede (private):** Replace pending submissions and cancel older unissued operations without abandoning issued I/O.
    - **CancelPending (private):** Discard pending submissions and unissued operations without claiming that issued I/O was recalled.
    - **Disarm (protected):** Atomically disarm and cancel unissued submissions without issuing motion commands.
    - **NotifyStateChanged (protected):** Notify subscribers of arming or observation changes without caching firmware state or allowing subscriber failures to interrupt execution.
    - **ConnectCoreAsync (protected abstract):** Open the provider connection, subscribe to observations, and obtain initial state without arming.
    - **StopCoreAsync (protected abstract):** Execute firmware-specific Stop and settle issued I/O before returning, propagating errors without claiming physical motion confirmation.
    - **DisconnectAsync (protected abstract):** Release provider subscriptions and the connection after issued work settles, including after incomplete connection setup.
  - **`Submission.cs`** - internal execution bookkeeping without firmware settings or command types.
  - **`SubmissionResult.cs`** - completed-write count and submission outcome, not confirmation of physical motion.
  - **`SubmissionOutcome.cs`** - distinguish completed, superseded, and cancelled submissions.
  - **`DeviceWriteException.cs`** - extends `IOException`; retain failed operation position, completed-write count, and underlying error without OEM types or rollback claims.
  - **`Log.cs`** - own shared execution diagnostics using existing structured logging conventions, excluding raw payloads and sensitive identifiers.

  Caller cancellation affects unissued operations only; issued I/O remains owned
  until settled. An unissued Stop may be cancelled without rearming. Firmware
  packages supply operations, not a second scheduler or parallel state machine.

- **`OssmSharp.Runtime.Oem`**
  - **`IOemDevice.cs`** - extends `IOssmDevice` with OEM-typed requests and state.
    - **SetAsync:** Submit complete OEM settings through ToCommands and shared SubmitAsync, returning outcome and completed-write count without claiming physical motion.
    - **GetStateAsync(bypassCache):** Return cached OEM state, or serialize ReadStateAsync and UpdateState through ExecuteAsync for live reads, propagating read failures.
  - **`MotionSettings.cs`** - complete OEM pattern identifier, speed, stroke, depth, and sensation; validate basic ranges without interpreting pattern behavior.
    - **ToCommands (internal):** Expand settings into OEM assignments ordered by pattern, stroke, depth, sensation, and speed.
  - **`DeviceControl.cs`** - identify the five OEM controls, not an extensible cross-firmware registry.
  - **`DeviceCommand.cs`** - represent one OEM control assignment with basic range validation.
  - **`DeviceState.cs`** - carry last observed OEM settings without inferred expiry, physical position, or safety claims.
  - **`OssmDevice.cs`** - extends `DeviceBase` and implements `IOemDevice`.
    - **ConnectCoreAsync (protected override):** Use Shiny Bluetooth to connect and subscribe, then read initial OEM state through ReadStateAsync and UpdateState.
    - **StopCoreAsync (protected override):** Send the OEM speed-zero assignment through WriteCommandAsync after recording its expectation.
    - **DisconnectAsync (protected override):** Release OEM Bluetooth subscriptions and disconnect the owned connection.
    - **WriteCommandAsync(command) (private):** Encode one DeviceCommand through Request.Encode and write it through Shiny Bluetooth, awaiting issued I/O without accepting or decomposing MotionSettings.
    - **ReadStateAsync (private):** Read live OEM controls through Bluetooth and DecodeState, propagating malformed responses and provider errors.
    - **DecodeState (private):** Validate and decode OEM wire responses into DeviceState without inferring physical motion or safety.
    - **UpdateState (private):** Store the single last known state, detect external changes, invoke Disarm when unexplained, and notify subscribers without arming.
    - **Expect (private):** Record expectations only for commands being issued, coordinating with observation handling rather than registering all planned commands.
    - **DetectExternalChange (private):** Compare observations against OEM expectations, accepting delayed issued assignments and identifying unexplained changes without interpreting pattern behavior.
  - **`DeviceDiscovery.cs`**
    - **ScanAsync:** Discover distinct devices advertising the OEM service within a bounded scan, returning identifiers and names while surfacing cancellation and Bluetooth errors.
  - **`DiscoveredDevice.cs`** - platform device identifier and advertised name for selection.
  - **`Firmware.cs`** - OEM service identifiers, characteristic identifiers, and relevant wire constants.
  - **`Request.cs`**
    - **Encode(command):** Encode one OEM assignment into its wire grammar, rejecting unrepresentable values without clamping, rounding, or remapping patterns.
  - **`BleServiceCollectionExtensions.cs`**
    - **Register:** Register discovery and device creation with caller-owned disposal and standard dependency injection lifetimes.

  OEM submission operations pair Expect with WriteCommandAsync only when the
  shared runtime issues that command. Runtime owns ordering; OEM owns expectation
  semantics and synchronization with observations. A valid pattern identifier
  does not guarantee that a firmware version implements it. The library
  validates basic ranges and wire representation, not pattern-specific inputs.

- **`OssmSharp.Runtime.SomethingElse`** - placeholder; no speculative files or functions are required.
  - Adopt the common lifecycle only when the actual firmware supports its submission, Stop, cancellation, and cleanup guarantees.
  - Own typed settings, state, and primitives; a request may produce one aggregate operation and Stop may use a dedicated command.
  - Supply firmware-specific observation interpretation without borrowing OEM control-expectation rules.
  - Replace the placeholder name and specify members only after selecting the firmware contract.

- **`OssmSharp.Cli`**
  - **`Program.cs`**
    - **Main:** Parse launch arguments, compose dependencies, and invoke listing or the prompt with the appropriate exit code.
    - **ListAsync:** Discover OEM devices and print their identifiers and names without connecting.
    - **PromptAsync:** Select the explicit identifier or sole discovered device, connect, run the prompt, and await disposal on exit or connection failure.
  - **`PromptHandler.cs`**
    - **RunAsync:** Read commands while remaining responsive to newer settings, Stop, and shutdown.
    - **Set:** Parse complete OEM settings, submit through IOemDevice, and display completion, supersession, cancellation, or partial failure with armed status.
    - **Stop:** Request emergency Stop and display its outcome, pending-work cancellation, and armed status without claiming rollback.
    - **Reset:** Explicitly reset the device and display armed status without resubmitting previous settings.
    - **StatusAsync(bypassCache):** Display cached or live OEM state as requested, distinguishing its source and including armed status.
    - **Help:** Describe OEM command syntax and basic argument ranges without presenting pattern identifiers as cross-firmware identities.
    - **Quit:** End command intake and await owned operations and cleanup.

  The five-argument set command remains OEM-specific. Multi-firmware consumers
  need explicit firmware-specific input and presentation; no generic settings
  dictionary, casts, or universal pattern API are introduced.

## Consumer call chains (examples)

- **Discover, connect, and open the prompt:**
  - **Input:** `OssmSharp.Cli prompt`, optionally with `--device <id>`.
  - **Composition:** `Program.Main` -> `BleServiceCollectionExtensions.Register` -> `Program.PromptAsync` -> `DeviceDiscovery.ScanAsync` -> Shiny Bluetooth scan.
  - **Connection:** `Program.PromptAsync` selects a device -> `IOssmDevice.ConnectAsync` (`DeviceBase` implementation) -> `OssmDevice.ConnectCoreAsync` -> Bluetooth connection and subscription -> `OssmDevice.ReadStateAsync` -> `OssmDevice.DecodeState` -> `OssmDevice.UpdateState`.
  - **Return:** Connected, disarmed OEM device -> `PromptHandler.RunAsync`; explicit `PromptHandler.Reset` -> `IOssmDevice.ResetAsync` permits later settings without sending motion.
  - **Failure:** Ambiguous discovery exits without connecting; connection errors propagate and cleanup is awaited through `IOssmDevice.DisposeAsync` -> `OssmDevice.DisconnectAsync`.

  ```mermaid
  sequenceDiagram
      autonumber
      participant CLI as CLI (Program / PromptHandler)
      participant Discovery as OEM DeviceDiscovery
      participant Runtime as Shared DeviceBase
      participant Oem as OEM OssmDevice
      participant Bluetooth as Shiny Bluetooth

      Note over CLI: prompt [--device id]<br/>Program.Main calls BleServiceCollectionExtensions.Register<br/>then Program.PromptAsync
      Note over Runtime,Oem: Base and override on the same device instance<br/>Not separate services
      CLI->>Discovery: ScanAsync()
      Discovery->>Bluetooth: Scan for the OEM service
      Bluetooth-->>Discovery: Matching devices
      Discovery-->>CLI: Device identifiers and names
      Note over CLI: Select explicit identifier or sole device<br/>Ambiguous discovery exits without connecting

      CLI->>Runtime: IOssmDevice.ConnectAsync()
      Runtime->>Oem: ConnectCoreAsync()
      Oem->>Bluetooth: Connect, subscribe, and read via ReadStateAsync()
      Bluetooth-->>Oem: Initial OEM controls
      Oem->>Oem: DecodeState() then UpdateState()
      Oem-->>Runtime: Initialization complete
      Runtime-->>CLI: Connected and disarmed
      CLI->>CLI: PromptHandler.RunAsync()

      Note over CLI,Runtime: Explicit reset: PromptHandler.Reset calls IOssmDevice.ResetAsync<br/>Arms submission without sending motion settings
      Note over CLI,Bluetooth: On connection failure or prompt exit: await IOssmDevice.DisposeAsync<br/>Disarm, cancel unissued work, and await issued I/O<br/>Then OssmDevice.DisconnectAsync releases subscriptions and connection
  ```

- **Submit complete OEM settings:**
  - **Input:** An armed device and `set <pattern> <speed> <stroke> <depth> <sensation>` with all five arguments.
  - **Consumer intent:** `PromptHandler.Set` -> `IOemDevice.SetAsync` (`OssmDevice` implementation) with one complete `MotionSettings` value.
  - **OEM decomposition:** `MotionSettings.ToCommands` produces ordered OEM `DeviceCommand` assignments, not motor instructions.
  - **Execution:** `DeviceBase.SubmitAsync` -> `DeviceBase.Supersede` -> `DeviceBase.ExecuteAsync` -> each surviving operation calls `OssmDevice.Expect` then `OssmDevice.WriteCommandAsync`.
  - **Transport:** `OssmDevice.WriteCommandAsync` -> `Request.Encode` -> Shiny Bluetooth write -> OEM firmware executes its pattern.
  - **Return:** Completed primitive writes -> `SubmissionResult` -> CLI displays outcome and armed status, without claiming verified physical motion.
  - **Supersession or failure:** Runtime discards unissued older operations and awaits issued I/O; write errors propagate as `DeviceWriteException` with known progress and leave submission disarmed.
  - **Feedback:** Firmware notification -> `OssmDevice.DecodeState` -> `OssmDevice.UpdateState` -> `OssmDevice.DetectExternalChange` -> `DeviceBase.Disarm` when unexplained -> `DeviceBase.NotifyStateChanged` -> `StateChanged`.

  ```mermaid
  sequenceDiagram
      autonumber
      participant CLI as CLI PromptHandler
      participant Oem as OEM OssmDevice
      participant Runtime as Shared DeviceBase
      participant Bluetooth as Shiny Bluetooth

      Note over CLI: Connected and armed<br/>Set parses all five OEM arguments
      Note over Oem,Runtime: Firmware implementation and shared execution owner<br/>on the same device instance
      CLI->>Oem: IOemDevice.SetAsync(MotionSettings)
      Oem->>Oem: MotionSettings.ToCommands()
      Note over Oem: Pattern, stroke, depth, sensation, then speed
      Oem->>Runtime: SubmitAsync(ordered command operations)
      Runtime->>Runtime: Supersede() then ExecuteAsync()

      loop While commands remain and submission is active
          Runtime->>Oem: Expect(command) then WriteCommandAsync(command)
          Oem->>Bluetooth: Request.Encode(command) and write
          Bluetooth-->>Oem: Issued I/O settles
          Oem-->>Runtime: Command completed
      end

      Runtime-->>Oem: SubmissionResult
      Oem-->>CLI: Outcome and completed-write count
      Note over CLI,Runtime: Supersession or cancellation discards unissued commands only<br/>Issued I/O is awaited. Completion does not verify physical motion
      Note over CLI,Bluetooth: A write failure stops the sequence and disarms submission<br/>DeviceWriteException preserves known progress. No rollback is implied
      Note over Oem,Bluetooth: Notifications may arrive during any stage<br/>DecodeState -> UpdateState -> DetectExternalChange<br/>Unexplained changes invoke Runtime.Disarm, then NotifyStateChanged<br/>Consumers receive StateChanged
  ```

- **Stop while preserving arming on success:**
  - **Input:** `stop` while settings may still be pending or executing.
  - **Consumer intent:** `PromptHandler.Stop` -> `IOssmDevice.EmergencyStopAsync` (`DeviceBase` implementation).
  - **Execution:** `DeviceBase` interrupts pending settings, invalidates queued Reset, and sends Stop without waiting for an earlier response.
  - **OEM primitive and transport:** `OssmDevice.Expect` records speed zero -> `OssmDevice.WriteCommandAsync` -> `Request.Encode` -> Shiny Bluetooth write.
  - **Return:** Stop write settles -> CLI displays success and accepts another submission without Reset when the device was already armed.
  - **Failure or cancellation:** Stop failure propagates and disarms. Cancellation before Stop starts prevents the write; neither outcome recalls issued I/O.

    ```mermaid
    sequenceDiagram
      autonumber
      participant CLI as CLI PromptHandler
      participant Runtime as Shared DeviceBase
      participant Oem as OEM OssmDevice
      participant Bluetooth as Shiny Bluetooth

      Note over CLI: PromptHandler.Stop<br/>Settings may be pending or executing
      Note over Runtime,Oem: Base and override on the same device instance
      CLI->>Runtime: IOssmDevice.EmergencyStopAsync()
      Runtime->>Runtime: Interrupt pending settings and queued Reset
      Note over Runtime: Current arming is preserved<br/>Unissued settings are discarded

      alt Stop is issued
        Runtime->>Oem: StopCoreAsync()
        Oem->>Oem: Expect(speed zero) then WriteCommandAsync(command)
        Oem->>Bluetooth: Request.Encode(command) and write speed zero
        Bluetooth-->>Oem: Stop I/O settles with success or failure
        Oem-->>Runtime: Completion or propagated error
        Runtime-->>CLI: Stop outcome. Success preserves prior arming; failure disarms
      else Cancellation prevents the unissued Stop
        Runtime-->>CLI: Cancellation. No Stop write issued
      end

      Note over CLI,Bluetooth: Neither failure nor cancellation recalls issued I/O<br/>Successful Stop never rearms an already-disarmed device
    ```
