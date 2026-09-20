# Device adapters: Shiny BLE and native simulation

Provide physical BLE and local simulation through the same `IOssmDevice`
contract, selected by dependency injection without changing application control
or feedback handling.

## Status and scope

The owner accepted the following direction on 20 September 2026:

- Use Shiny in the owning .NET process for physical BLE; no BLE helper process.
- Keep `tools/ossm-sim-bridge` and the native OSSM-RS motion engine for
  simulation.
- Select one device implementation through dependency injection.
- Normalize simulator data to the same application-facing types and units as
  stock-device data. Applications do not parse either transport.
- Remove superseded adapter references as part of integration, rather than keep
  aliases, compatibility shims or a second device path.
- Accept motion simulation without stock-firmware or BLE emulation for now.

This document records that direction and proposes the detailed configuration and
integration below. It does not declare those details implemented, select a
qualified host platform, or authorize physical operation. The
[device specification](spec.md) remains authoritative for `IOssmDevice` and
DC-01 through DC-09; this document adds no parallel safety requirements or IDs.
The [core](../control-core.md) and [application](../application.md) contracts
retain their ownership boundaries.

The audience is implementers of the device libraries and application
composition, and operators configuring a local device. Non-goals are a new
motion engine, Rust native interop, a BLE peripheral emulator, a plug-in
framework, runtime device switching, and additional patterns or physical
qualification.

## Current foundation

`StockOssmDevice` is the stock adapter with BLE still deferred. Its internal
`Request`, `Acknowledgement` and `StateResponse` records provide protocol
building blocks, not a complete connection or safe multi-write transition.
`SimulatedDevice` already consumes the native helper. Both derive from
`DeviceBase`; the shared API publishes transient `DeviceState` events rather
than caching complete device states.

Application composition and controller consumers still need integration with
that API. The existence of standalone adapter tests is not evidence that the
whole application already uses it.

The [helper contract](../../../tools/ossm-sim-bridge/README.md) pins
`ossm-motion` at `578161b2e78d8ca70f8909f0ec34b8ae6014cfda` (Apache-2.0). It
supplies a no-op motor and runtime scheduling around the upstream engine;
position and velocity come from that engine, not a second motion model. Its
bounded JSON Lines protocol also provides fault injection. It performs no BLE or
physical I/O.

## Scenarios and observable outcomes

| Scenario                                        | Expected result                                                                                                                                            |
| ----------------------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Start local simulation                          | One `SimulatedDevice` owns one native child. Ordinary application controls and feedback use the shared contract; Bluetooth is not accessed.                |
| Start with an enrolled physical device          | One `OssmDevice` resolves the configured peripheral, verifies live identity and discovers the required GATT surface. Connection alone does not arm motion. |
| Another nearby device has the same name         | It is not selected. Name, signal strength and discovery order do not override enrollment.                                                                  |
| Configured device is absent or identity differs | Startup connection fails without motion writes, substitution or fallback to simulation.                                                                    |
| Stop occurs during an outstanding write         | DC-05 governs settlement and the subsequent zero write. No concurrent replacement writer is created.                                                       |
| Simulator feedback freezes or the child exits   | Shared freshness/failure handling applies. Responsive status replies do not turn frozen samples into new feedback.                                         |
| Change implementation or enrolled identity      | Stop and dispose the existing owner, then restart with validated configuration. Old intent is not replayed.                                                |

## Composition and ownership

| Selected implementation | Device path                                                                                  | Resources owned by the adapter                                          |
| ----------------------- | -------------------------------------------------------------------------------------------- | ----------------------------------------------------------------------- |
| Physical                | `IOssmDevice` → `OssmDevice` → Shiny → native OS BLE → OSSM                                  | One peripheral connection, subscriptions and native-operation lifetimes |
| Simulator               | `IOssmDevice` → `SimulatedDevice` → private stdin/stdout → `ossm-sim-bridge` → `ossm-motion` | One child process, bounded request correlation and stream readers       |

Each implementation supplies its component-owned `IServiceCollection`
registration. Register the selected concrete device once as a singleton, then
resolve that same instance as both `IOssmDevice` and the hosted service. Do not
construct another instance in hosted-service registration, register both
implementations and choose the last, or select implementations inside consumers.
Reject conflicting registrations before starting either transport.

Shiny's `IBleManager` and platform registration belong only to the stock
implementation. The simulator can reference the shared base without registering
or resolving Shiny. The public abstractions contain no Shiny, Rust, process or
application-host types. Other library consumers may choose a registration
directly; they need not adopt the application's configuration hierarchy.

In the application, ControlService owns the controller and selected device. The
controller consumes `IOssmDevice`; ordinary service, UI and guest paths must not
branch on concrete adapter types. Profile/capability metadata may describe real
differences, including a simulator label, without selecting another control
algorithm. Simulator fault controls remain a separate trusted local
operator/test facility, never members of `IOssmDevice` or guest operations.

Lifetime ordering is explicit:

1. Construct the controller and attach state/connection handlers before starting
   the device hosted service, so initial transient events are not lost.
2. Start the selected adapter; apply DC-01 readiness separately from connection.
3. On shutdown, revoke authority and run the controller's bounded Stop and
   settlement handling before cancelling the adapter's transport lifetime.
4. Release subscriptions, readers and owned resources through one lifecycle
   owner. Unconfirmed stopping remains unconfirmed; DI aliasing does not make
   repeated disposal or shutdown ordering correct by itself.

### Startup configuration

The following are proposed application bindings, not new public device-interface
members. Bind typed options with DataAnnotations, cross-field validation and
`ValidateOnStart()` as required by the repository.

| Key                               | Meaning and validation                                                                                                                                                                       |
| --------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `OSSM:Device:Mode`                | Closed choice: `simulator` or `stock-ble`. Default to `simulator`; reject unknown values. Select once at composition time.                                                                   |
| `OSSM:Device:Ble:PeripheralId`    | Required in stock mode. Opaque Shiny/platform locator captured during enrollment; not a portable Bluetooth address.                                                                          |
| `OSSM:Device:Ble:ExpectedWifiMac` | Required in stock mode. Six-octet Wi-Fi MAC from the live identity characteristic, normalized for comparison. Verify before configuration or motion writes.                                  |
| `OSSM:Control:SimulatorPath`      | Existing simulator executable setting. In simulator mode, map it to `SimulatedDeviceOptions.ExecutablePath` and validate an absolute, existing executable. It is not required in stock mode. |

Retaining the existing simulator path setting avoids an unrelated configuration
rename; it does not retain a superseded adapter. Stock options belong to
`OssmDeviceOptions`; simulator options remain independently usable. Validate
only the selected implementation's required resources. Runtime reload must not
switch devices, restart the child or retarget an active connection.

Use the default OS Bluetooth adapter initially. Do not offer an `AdapterAddress`
option that the inspected Shiny Linux API cannot honor. UUIDs and command syntax
are fixed by the stock profile, not configurable strings.

### Build and orchestration

AppHost reads and validates `OSSM:Device:Mode` before constructing its resource
graph, then supplies that same value to ControlService as `OSSM__Device__Mode`.
ControlService validates it before DI selection. In simulator mode, Aspire
prepares the native executable and supplies its path; `SimulatedDevice` starts
and owns the only simulator child. Aspire must not also launch a second copy. In
stock mode, omit the Cargo build resource, its startup dependency, simulator
path injection and the child; supply the enrolled BLE configuration only to
ControlService. Physical operation requires no Rust toolchain.

Runtime mode selection does not by itself solve target-framework selection.
Retain a portable `net10.0` build path for the shared base and simulator. Native
Shiny targets/references are selected for physical-host builds without making
simulator-only restore/build require Apple or Windows workloads. Select the
appropriate target set before restore, not merely a framework after an
all-target restore. This must be demonstrated before claiming portable builds;
no new project split is prescribed here.

## Physical device selection and connection

### Enrollment

Enrollment is an explicit host-local setup phase before normal stock startup and
its required-identity validation. It has exclusive device ownership: no active
motion controller, adapter connection or unsettled device I/O. It is not a guest
endpoint, browser Web Bluetooth operation or additional helper process. The
presentation of this local setup operation is outside this adapter design.

The owning host selects setup-only composition before starting normal services:
register Shiny discovery/identity support, but neither the device hosted service
nor the motion controller. Do not bind the run-only options requiring an
enrolled identity in that composition. This is a distinct local setup operation,
not a third runtime device mode or a way to bypass normal startup validation.
End setup and release its resources before starting the normal service
composition.

1. Scan for service `522b443a-4f53-534d-0001-420badbabe69` within a bounded
   operator-controlled scan. Post-filter results as well as requesting a native
   service filter; the inspected Linux implementation does not apply its filter
   consistently to newly discovered devices.
2. Show candidates with their platform locator and advertised name; signal
   strength is informational. Require explicit selection even when names match.
3. Connect only to the selected candidate and read the live pairing/identity
   characteristic. Display identity/version for confirmation; issue no motion,
   navigation, rename, Wi-Fi provisioning or knob-configuration writes.
4. Save the confirmed locator and Wi-Fi MAC in local startup configuration, then
   release the setup connection before normal device startup.

The [pinned firmware evidence](spec.md#device-profile-identity-and-feedback)
defines the pairing read as `MAC;chip;wifiConnected;md5;version`. Its MAC is the
Wi-Fi MAC, not a Bluetooth MAC. The advertised name is mutable and the
firmware's Device Information System ID is not unique. Neither replaces the
enrolled pair. Changing host/adapter identity or losing the platform locator
requires explicit re-enrollment, not a search-and-replace fallback.

### Normal startup

Resolve the configured locator through Shiny's known-peripheral lookup where
available. A cache miss is not permission to choose another device: perform the
bounded service scan and match the exact configured locator. Discover the
required service/characteristics and verify the live Wi-Fi MAC against
configuration on every new connection. Discover firmware version and patterns;
do not mistake successful identification for physical qualification.

Subscribe to lifecycle/failure events before connecting. Own one attempt and
disable automatic reconnect. Publish connection and validated state through the
existing device contract. Controller initialization owns readiness and required
zero/knob-limit setup under DC-01; discovery alone must not perform navigation,
calibration or motion. On failure, release only settled resources, expose the
failure through the specified lifecycle/exception paths, and do not retry or
restore prior intent automatically.

### Shiny and the stock GATT profile

Use Shiny's BLE **central/client** APIs, not its peripheral-hosting APIs. The
inspected baseline is `Shiny.BluetoothLE` **5.7.2**, plus
`Shiny.BluetoothLE.Linux` **5.7.2** for Linux. Both MIT-licensed packages
identify source commit `1aec024c2a32711ce58a1174c0df69de02be0c52` in their
[base][shiny-package] and [Linux][shiny-linux-package] package metadata. This is
research provenance, not an installed or qualified dependency pin. Resolve the
findings below before choosing the implementation pin; keep that pin in central
package configuration.

All custom UUIDs below expand `522b443a-4f53-534d-XXXX-420badbabe69`:

| `XXXX`         | Use                                                                             |
| -------------- | ------------------------------------------------------------------------------- |
| `0001`         | OSSM service; discovery filter and required service                             |
| `0010`         | Live identity read; no pairing-write provisioning in this feature               |
| `1000`         | Command characteristic; serialize ordinary `set:*` writes with response         |
| `1010`         | Knob-limit configuration; enable and read back during authorized initialization |
| `2000`         | State notifications; cached reads and `ok:<command>` are not new state evidence |
| `3000`, `3010` | Pattern metadata; discovery does not expand the qualified pattern set           |

Reuse the stock protocol records rather than writing another protocol stack.
Their existence does not justify submitting a whole command list without
inter-write checks. Use plain characteristic payloads, not Shiny's optional
message framing: stock firmware does not implement that framing protocol.
Ordinary writes exclude navigation, position streaming and report-only fields.

Attach the state callback before enabling notifications. Validate and publish
events in order; do not debounce or coalesce away intermediate speed changes.
Bound any adapter-owned queue and fault on overflow rather than silently losing
evidence. Preserve receive-time provenance through processing; buffered values,
repeated cached data and acknowledgements must not become fresh feedback. Use
pairing/configuration reads for responsiveness rather than polling the
subscribed state value. A provider's value-changed callback is not by itself
proof that a new motion sample was generated.

## Transparent simulator data

Transparency means a common typed device contract, not identical transport bytes
or fabricated hardware behavior. Normalize native samples directly into
`DeviceState` and `MotionSettings`; do not serialize stock JSON and parse it
again, emulate GATT, or route simulation through Shiny.

| Contract data                  | Mapping and limits                                                                                                                                                                                                                  |
| ------------------------------ | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Motion settings                | Same enums, integer percentages and millimetre units as the stock adapter. Read actual native-engine controls and position, not the last request sent by .NET.                                                                      |
| Mode and unsupported fields    | Use an explicit simulator-profile mode mapping. The initial profile has no stock navigation/calibration state machine and supports buffer `0` only; unsupported nonzero buffer requests must reject rather than silently disappear. |
| `Timestamp`                    | UTC observation timestamp established at receipt, preserved through publication. Firmware uptime remains private metadata; no cached/frozen sample is restamped as new evidence.                                                    |
| `SessionId`                    | `Guid.Empty` when the device/profile reports no session. It is never a guest or application session identifier.                                                                                                                     |
| `VelocityMillimetresPerSecond` | Actual native debug output for simulation; `null` for stock when unreported. Consumers handle absence without a concrete-type branch.                                                                                               |
| `DeviceInformation`            | Honest discovered identity, version and capabilities. Retain `ossm-rs-sim-v1`; do not advertise stock firmware or qualification.                                                                                                    |
| Errors and connection          | Same public events, semantic exceptions and lifecycle behavior. Native protocol fields and correlation IDs remain private.                                                                                                          |

The initial supported motion profile remains Simple Stroke with sensation `50`.
The simulator's periodic samples, fault controls and possible retract-on-disable
are useful but do not reproduce stock notification cadence or stopping behavior.
This limitation is accepted; it is not a reason to remove local simulation or
weaken the common contract. Tests of BLE transport and physical behavior remain
separate from motion simulation.

## Compatibility findings and integration gates

The following issues block the affected integration or physical support claim,
not the accepted DI/simulator architecture. Source inspection is not runtime
qualification. The project owner must approve any requirement or public-API
change instead of resolving a gate by silently relaxing the contract.

| Issue and evidence                                                                                                                                                                 | Impact and next step                                                                                                                                                                                                                                                                                                   |
| ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| [Windows Shiny source][shiny-windows] ignores `ConnectionConfig` and retains `GattSession.MaintainConnection` across transient loss.                                               | `AutoConnect: false` does not satisfy the no-reconnect rule on this baseline. Verify a corrected release or narrowly reviewed correction and test loss/reappearance. Do not release unsettled native I/O merely to force disconnect.                                                                                   |
| [Linux notifications][shiny-linux] ignore D-Bus subscription-completion signals; [macOS notifications][shiny-macos-notify] do not propagate characteristic-update callback errors. | Verify broken-subscription/error behavior, stale-value rejection and notification setup/teardown completion. A subscription object alone does not establish successful setup. Correct the provider behavior where needed; retain DC-07 failure handling.                                                               |
| Provider cancellation/disposal can end an observer before native work settles.                                                                                                     | Preserve a completion observer/task for issued I/O independently of the caller's wait. Prove timeout, late completion and shutdown ownership on each selected backend; never equate an Rx unsubscribe with native settlement.                                                                                          |
| Stock host platforms have not been qualified.                                                                                                                                      | Verify Windows platform-targeted assets, Linux BlueZ/D-Bus permissions, and native macOS targeting/privacy/callback dispatch inside the actual service host. Shiny has macOS support; compatibility with this ASP.NET executable remains unproved, not ruled out. Record OS, account, adapter and deployment evidence. |
| The current shared dispatch hook receives the service-lifetime token, not a controller check before each GATT write.                                                               | Define and verify private controller/adapter coordination for DC-04/DC-05 before enabling multi-write motion. A whole-request semaphore or cancelled caller wait is insufficient. Keep `IOssmDevice` unchanged unless a separately reviewed API change is necessary.                                                   |
| Current simulator dispatch sends every zero-speed request as native `stop`, which preserves other fields; native `write` can configure a stopped tuple.                            | Resolve how Stop preserves current settings while intentional stopped-envelope changes reach the engine under DC-06. Do not silently discard those changes or let a stopping operation restore stale settings. Prove both through the controller/adapter boundary before declaring integration complete.               |
| Stock `1.0.34` has change-only cached state and no effective periodic heartbeat.                                                                                                   | Sustained motion may not satisfy DC-07 freshness even with working BLE. Establish adequate feedback evidence or seek an explicit scope/requirement decision; do not freshen cached reads, lengthen deadlines to conceal failure, or claim the simulator proves compatibility. Physical release remains blocked.        |

The platform build experiment must also prove simulator-only restore/run without
native BLE workloads or permissions. A `macos26.0` package target denotes an API
target; it does not by itself establish a minimum deployed macOS version. Use an
owned CoreBluetooth dispatch queue appropriate to the service host and verify
its lifetime rather than assume a GUI event loop. None of these gates calls for
another BLE process.

## Security and operational behavior

Enrollment prevents accidental selection, not impersonation. The reported Wi-Fi
MAC and version are not authentication or attestation; stock firmware does not
supply an authenticated exclusive controller lease. Keep the existing
supervised-use and independent-stopping qualification boundaries.

Treat advertisements, characteristic values and simulator output as untrusted
bounded inputs. Keep device identifiers and raw payloads out of routine logs and
guest projections. Use existing structured `ILogger`/ServiceDefaults diagnostics
for selected mode, connection phases, semantic failures and owned-child failure,
without creating another logging subsystem or duplicating lifecycle failures.
Native Bluetooth access belongs to the owning host identity, not either web
frontend. Simulator pipes are private to the child; no network listener is
added.

## Delivery and verification

1. Integrate the current device contract with controller/service composition and
   one hosted-instance lifetime. Remove superseded types, namespaces, imports,
   project references, registrations, fixtures and documentation in the same
   integration change. Do not leave aliases or a selectable second adapter path.
   Retain the valid `ossm-sim-bridge` executable name and protocol.
2. Demonstrate the application through `SimulatedDevice`, including stopped
   configuration, delayed completion, external changes, frozen feedback and
   child failure. Keep fault injection isolated from the guest contract.
3. Verify the intended Shiny host targets and provider corrections before wiring
   physical motion. Add enrollment, configuration validation and read-only
   identity/discovery under exclusive ownership first.
4. Add serialized stock writes and ordered state handling with integrated
   revocation/settlement checks. Only separately authorized qualification can
   enable a physical profile.

Acceptance evidence extends the existing
[conformance requirements](spec.md#software-timing-and-conformance):

- DI tests prove reference equality of the hosted device and `IOssmDevice`, one
  selected owner, pre-start event subscriptions and shutdown ordering.
- Composition tests prove stock mode has no simulator/Cargo dependency and
  simulator mode neither initializes Bluetooth nor needs BLE identity options.
- Common contract/controller tests use the same typed states and operations for
  both implementations. Deterministic fakes prove policy and timing; native
  simulator integration proves the Rust boundary, not BLE semantics.
- Selection tests cover duplicate names, absent locators, identity mismatch,
  cache misses, invalid configuration and no fallback or automatic reconnect.
- Controller-to-adapter tests revoke authority or latch Stop between individual
  writes, then deliver late completion. Only the permitted zero operation may
  follow; no replacement connection or writer is admitted prematurely.
- Provider tests cover real platform lifecycle, notification errors, overload,
  cached-value provenance and native settlement. Fakes cannot certify these.
- Repository checks find no superseded adapter references in maintained source,
  tests, project/configuration files or documentation. Update architecture views
  with implemented project/ownership changes, without retaining obsolete paths.

This documentation change does not run those checks or implement that removal.
Review first confirms the proposed configuration/enrollment details and resolves
the private Stop/configuration and per-write coordination gates. Physical
support then requires evidence against the existing
[qualification gates](spec.md#physical-qualification).

## Alternatives considered

| Alternative                                      | Reason not selected                                                                                                                                                |
| ------------------------------------------------ | ------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| Rust BLE helper                                  | Adds a process and protocol boundary for operations Shiny can perform in-process; conflicts with the accepted deployment direction.                                |
| Embed the simulator through a C ABI              | Removes a child but adds native ABI, memory/error ownership and runtime-lifecycle work. No demonstrated need justifies replacing the existing bounded integration. |
| Replace native simulation with a C# motion model | Duplicates upstream behavior and loses direct engine execution. Small deterministic test fakes remain useful, but are not its replacement.                         |
| Drop simulation or emulate BLE instead           | Dropping it removes hardware-free development; BLE emulation adds a different testing project. Retain useful motion simulation with an explicit fidelity limit.    |

[shiny-package]:
  https://api.nuget.org/v3-flatcontainer/shiny.bluetoothle/5.7.2/shiny.bluetoothle.nuspec
[shiny-linux-package]:
  https://api.nuget.org/v3-flatcontainer/shiny.bluetoothle.linux/5.7.2/shiny.bluetoothle.linux.nuspec
[shiny-windows]:
  https://github.com/shinyorg/shiny/blob/1aec024c2a32711ce58a1174c0df69de02be0c52/src/Shiny.BluetoothLE/Platforms/Windows/Peripheral.cs
[shiny-linux]:
  https://github.com/shinyorg/shiny/blob/1aec024c2a32711ce58a1174c0df69de02be0c52/src/Shiny.BluetoothLE.Linux/Peripheral.cs
[shiny-macos-notify]:
  https://github.com/shinyorg/shiny/blob/1aec024c2a32711ce58a1174c0df69de02be0c52/src/Shiny.BluetoothLE/Platforms/MacOS/Peripheral_Characteristics.cs
