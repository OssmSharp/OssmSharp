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

The accepted 25 September 2026 design supersedes the child-process transport and
project packaging: direct CLI and Web.Api owners select isolated providers;
Device.Runtime holds shared mechanics and the simulator is independently
started. This document defines that target. It does not declare it implemented, select a
qualified host platform, or authorize physical operation. The
[device specification](spec.md) remains authoritative for `IOssmDevice` and
DC-01 through DC-09; this document adds no parallel safety requirements or IDs.
The [web-control design](../web-control-design.md#requirement-ownership) records
core and application ownership, including the
[reusable core boundary](../web-control-design.md#reusable-core-boundary).
Its direct-device topology is accepted; implementation and physical operation
remain separately authorized work.

The audience is implementers of the device libraries and application
composition, and operators configuring a local device. Non-goals are a new
motion engine, Rust native interop, a BLE peripheral emulator, a plug-in
framework, runtime device switching, and additional patterns or physical
qualification.

## Current foundation

This section describes the pre-migration baseline. Its daemon/stdio behavior
must not be carried into the accepted target merely because it exists today.

`StockOssmDevice` is the stock adapter with BLE still deferred. Its internal
`Request`, `Acknowledgement` and `StateResponse` records provide protocol
building blocks, not a complete connection or safe multi-write transition.
`SimulatedDevice` already consumes the native helper. Both derive from
`DeviceBase`; the shared API publishes transient `DeviceState` events rather
than caching complete device states.

The simulator library owns an `AddOssmSimulator` registration that exposes one
instance as `SimulatedDevice`, `IOssmDevice` and `IHostedService`. The standalone
`OSSMDotNet.DeviceCli` can start one per-user background diagnostic host, query
status, issue bounded Simple Stroke motion and stop that host without ControlCore
or either web application. Its private current-user IPC exists only to manage
the diagnostic process; it is not an application control transport, remote
endpoint or authority boundary. Start never restores prior motion, bounded
motion settles at zero, and Stop settles zero before ending the simulator
process. The stock library includes strict codecs for the pinned identity and
pattern metadata payloads; its BLE motion transport remains deferred. The
standalone CLI now has an opt-in macOS-targeted, Shiny-backed read-only `identify`
command. Without a locator, it collects up to 64 distinct service-advertising
candidates during a five-second scan, shows numbered names and platform locators
on an interactive terminal, and requires a numeric choice. An explicitly supplied
locator uses a direct bounded scan. It reads only the selected device's live
identity and disconnects without any GATT writes. This does not implement
enrollment, normal stock-device startup or motion.
Only a compile-only native check has passed; the full Xcode-backed build and
device behavior remain unverified.

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
| Start local simulation | The operator/AppHost starts the simulator. One selected adapter connects to its configured loopback endpoint; Bluetooth is not accessed. |
| Close a client | Its Stop/cleanup path closes the connection; the simulator remains alive at zero for a later explicit connection. |
| Start with a selected physical device | One `StockOssmDevice` resolves the selected platform peripheral ID, validates live identity and discovers the required GATT surface. Connection alone does not arm motion. |
| Another nearby device has the same name | It is not selected by signal strength or discovery order; exact name selection must be unambiguous and resolve to one frozen platform ID. |
| Selected device is absent or differs | Connection fails without motion writes, substitution or fallback to simulation. A different reported Wi-Fi MAC alone does not fail connection. |
| Stop occurs during an outstanding write         | DC-05 governs settlement and the subsequent zero write. No concurrent replacement writer is created.                                                       |
| Simulator feedback freezes or the server exits | Shared freshness/failure handling applies. Responsive status replies do not turn frozen samples into new feedback. |
| Change implementation or selected peripheral | Stop and dispose the existing owner, then restart with the newly selected platform ID. Old intent is not replayed. |

## Composition and ownership

Every service must use dependency injection, and each device library must own
its DI registrations and lifetime decisions. This includes Primitives, which
may depend on DI abstractions while remaining independent of hosting and
concrete providers. Hosts compose library registration extensions rather than
repeating their service construction. Service-provider access belongs only in
composition factories, not consuming application or device logic.

Models, records, identifiers, settings and reported states remain ordinary
values. DI-managed factories may create selected-device and execution services,
but the caller must retain explicit ownership through issued I/O and cleanup.
DI must not capture or prematurely dispose those session-owned results.

### Provider-neutral selection and information

The CLI selection boundary uses one injected `IDeviceProvider` from Primitives.
`GetAvailableDevicesAsync` returns `DeviceIdentifier`
records exposing `Id` and `Name`; any native selection state remains private to
the provider implementation. Generic consumers pass the original identifier to
information reads and selected-device creation, without inspecting, casting or
reconstructing provider state. The provider validates compatibility and the
selected identity before I/O.

CLI discovery prints `Detecting devices. Please wait ...` before scanning and
`No devices found.` when the scan completes without matches. A discovery
failure still returns its error and nonzero exit code after the progress line.

An identifier does not establish a connection, readiness or authority. Its
validity is limited to its owning composition, and it owns no live connection.
`GetDeviceAsync` returns `DeviceInformation`, including the original selected
identifier and any reported Wi-Fi MAC, chip, Wi-Fi state, sketch digest and
version. Identity-only reads have no verified profile (`ProfileId` is null)
and no supported patterns. Connected-device information uses the same record
with its verified profile and capabilities. Neither the shared record nor
identity retrieval establishes motion readiness. The CLI owns formatting.

Stock registers discovery, information reading and the selected-device factory
through its `IServiceCollection` extension. Native transport services are
injected, not constructed by consuming device constructors. Per-operation
connections retain explicit ownership and awaited cleanup. CLI platform
extensions live under Interop, preserving lazy native-manager creation and
caller-owned macOS dispatch-queue lifetime.

The [CLI composition plan](../../plans/cli-platform-interop.md) owns the current
implementation sequence. Native command availability requires the execution
guarantees in DC-01 through DC-09; this selection design alone does not enable
motion or establish software conformance.

| Selected implementation | Device path                                                                                  | Resources owned by the adapter                                          |
| ----------------------- | -------------------------------------------------------------------------------------------- | ----------------------------------------------------------------------- |
| Physical                | `IOssmDevice` → `OssmDevice` → Shiny → native OS BLE → OSSM                                  | One peripheral connection, subscriptions and native-operation lifetimes |
| Simulator | `IOssmDevice` to `SimulatedDevice` to loopback TCP JSONL to `ossm-sim-bridge` to `ossm-motion` | One client connection, bounded request correlation and stream readers; no process ownership |

Each implementation supplies its component-owned `IServiceCollection`
registration. For a hosted device lifetime, register the selected concrete
device once and resolve that same instance as both `IOssmDevice` and the hosted
service. For the foreground CLI, register a selected-device factory; the prompt
owns and disposes its returned device rather than making DI capture an
operation-owned instance. Do not construct another instance in hosted-service
registration, register both implementations and choose the last, or select
implementations inside consumers. Reject conflicting registrations before
starting either transport.

Shiny's `IBleManager` and platform registration belong only to Device.Stock.
Device.Simulator references the provider-neutral base in Device.Runtime, never
Stock or Shiny. The public Primitives contain no Shiny, Rust, process or
application-host types. Other library consumers may choose a registration
directly; they need not adopt the application's configuration hierarchy.
Native hosts call `AddStockBluetooth()` on Windows or
`AddStockBluetooth(dispatchQueue)` on macOS. On macOS the host creates and owns
the `CoreFoundation.DispatchQueue`, keeping it alive through service-provider
disposal and settled BLE cleanup; neither the adapter nor discovery creates a
second container or assumes a GUI event loop. Use the same registered
`IBleManager` for the injected `IDeviceProvider` and selected-device factory.
Registering the manager does not scan, connect
or grant device access. The portable build contains neither native registration
nor a Shiny dependency.

For native macOS Stock builds, the shared build configuration suppresses
`XCODE_27_0_PREVIEW`. The Xcode 27 bindings mark even longstanding dispatch-queue
APIs as experimental. This centrally approved exemption includes queue-lifetime
tests; it does not suppress other diagnostics or establish physical qualification.

In web use, Web.Api owns Web.Application's controller and selected device. Cli
is a direct-device consumer and uses no web controller. The controller consumes
`IOssmDevice`; ordinary service, UI and guest paths must not branch on concrete
adapter types. Profile/capability metadata may describe real differences,
including a simulator label, without selecting another control algorithm.
Simulator fault controls remain a separate trusted local operator/test
facility, never members of `IOssmDevice` or guest operations.

Lifetime ordering is explicit:

1. Construct the controller and attach state/connection handlers before starting
   the device hosted service, so initial transient events are not lost.
2. Start the selected adapter; apply DC-01 readiness separately from connection.
3. On shutdown, revoke authority and run the controller's bounded Stop and
   settlement handling before cancelling the adapter's transport lifetime.
4. Release subscriptions, readers and connections through one lifecycle owner.
  Never stop the independently started simulator process. Unconfirmed stopping
  remains unconfirmed; DI aliasing does not make repeated disposal/shutdown
  ordering correct by itself.

Both owners use Device.Runtime's single interruption latch and cleanup path.
DeviceBase publishes state/semantic errors; it does not implement guest policy.
Web.Application checks admin/authority before delegating acknowledgement to the
same runtime latch/revision that the CLI acknowledges directly. Hardware Stop
and unexpected observed changes never cause restoration of old settings.

### Stock qualification and dispatch composition

DC-02 and DC-03 require software dispatch to remain separate from physical
qualification. A Stock-internal service resolves an immutable mapping for the
verified connected identity/profile. The adapter retains it only for that
connection and clears it on disconnect. Default composition has no qualified
mapping; discovery, identity reads and simulator results cannot create one.
There is no public configuration switch that bypasses qualification.

Stock implements a Runtime-owned transition-validation capability. Runtime
uses it to reject the desired tuple and any invalid intermediate before
creating pending work. Stock repeats validation at dispatch and supplies only
exact validated values to motion encoding. A mapping that would require
unqualified rounding or clamping rejects the request. A missing mapping
rejects envelope edits and nonzero output without writes, while explicit Stop
and initialization can still issue speed-only zero.

Each mapping must explicitly bound requested speed as well as travel and
keepout geometry; qualifying an envelope does not qualify every speed.
Its minimum and maximum endpoint bounds are measured in StrokeEngine coordinates:
engine zero is 6 mm inward from the physical home switch, not the rail origin.
The calibrated travel is the full measured home-to-far-switch span; the engine's
upper endpoint is at most that span minus both 6 mm keepouts.
Connection matching uses stable firmware and device identity fields and
supported-pattern values, not collection reference equality, a platform locator
or changing Wi-Fi connection status. A speed-only safety Stop remains available
outside the play-ready state and must not be rejected as an envelope change.
Runtime identifies that operation separately from ordinary settings writes. Stock
sends only zero through the same serialized transport owner even if a newer
report changes geometry after Runtime derived the Stop tuple; ordinary edits
still require transition and mapping validation before any write.

For the pinned Simple Stroke implementation, the configured scale is
[20 steps/mm](https://github.com/KinkyMakers/OSSM-hardware/blob/4871a84c54ae0734b1f8bc3d02fcc0098f12fbad/Software/src/constants/Config.h#L19-L34).
[Percentage conversion](https://github.com/KinkyMakers/OSSM-hardware/blob/4871a84c54ae0734b1f8bc3d02fcc0098f12fbad/Software/src/ossm/stroke_engine/stroke_engine.cpp#L23-L59)
uses single-precision arithmetic against calibrated travel and a 6 mm keepout
at each end. The engine truncates depth and stroke independently to integer
steps before clipping targets. Qualification must check the intended step
coordinates against that conversion and reject truncation or clipping, rather
than assuming whole-millimetre resolution or adding an arbitrary tolerance.
This source evidence establishes software conversion, not calibration accuracy
or physical qualification of a unit.

The Runtime authority revision and synchronous guard travel with each pending
request. Authority changes revoke its operation token immediately; the provider
checks revocation before each command while separately retaining ownership of
already-issued I/O. Acknowledgement or initialization never restores an earlier
tuple. Deterministic tests may substitute the qualification and BLE boundaries
to prove the real software path; they must not populate the production
qualification allowlist or claim physical safety.

### Startup configuration

The following are selected startup bindings, not new public device-interface
members. Bind typed options with DataAnnotations, cross-field validation and
`ValidateOnStart()` as required by the repository.

Implementation note: the current CLI selects its provider through prompt
arguments and uses a fixed five-second cleanup budget. It does not yet bind
`OSSM:Device:ShutdownTimeout`; the table describes the target configuration,
not proof that each binding is implemented.

| Key                               | Meaning and validation                                                                                                                                                                       |
| --------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `OSSM:Device:Mode`                | Closed choice: `simulator` or `stock-ble`. Default to `simulator`; reject unknown values. Select once at composition time.                                                                   |
| `OSSM:Device:Ble:PeripheralId`    | Required in stock mode. Opaque selected Shiny/platform locator; not a portable Bluetooth address. The CLI accepts a selected ID or resolves one exact unambiguous discovered name to its ID. |
| `OSSM:Control:SimulatorEndpoint` | Required for Web.Api's simulator: `tcp://127.0.0.1:<port>`, port 1-65535, no credentials/path/query/fragment. This identifies an already-started server, not an executable to launch. |
| `OSSM:Device:ShutdownTimeout` | Overall device cleanup budget, default five seconds, positive and finite. Web host shutdown allowance must exceed it. |

Remove `OSSM:Control:SimulatorPath` rather than retaining a fallback/alias; WIP
backwards compatibility is waived. Stock/simulator options remain independently
usable. Validate only the selected implementation's resources and reject a
runtime mode absent from the build. Runtime reload must not switch devices,
restart a process or retarget an active connection. Primitives remains free of
configuration/hosting dependencies.

Use the default OS Bluetooth adapter initially. Do not offer an `AdapterAddress`
option that the inspected Shiny Linux API cannot honor. UUIDs and command syntax
are fixed by the stock profile, not configurable strings.

### Build and orchestration

One optional AppHost has Simulated/Actual launch configurations. The
`DeviceProvider=Simulator|Stock` build property selects dependencies before
restore; each configuration builds the matching profile before launching it.
Simulator is the default. AppHost supplies matching `OSSM:Device:Mode` and only
the selected provider's options to Web.Api. Runtime mismatch fails startup.

Simulated prepares/starts the native server as an independent resource, waits
for its listener and supplies its endpoint. Actual omits simulator/Cargo work
and supplies a selected BLE peripheral ID; AppHost itself never opens BLE. Direct CLI
or API startup needs no AppHost; the operator starts the simulator separately.
No client starts a private daemon or owns simulator shutdown. Production
logging and configured telemetry use Defaults, not the development dashboard.

Runtime mode selection does not by itself solve target-framework selection.
Retain a portable `net10.0` build path for the shared base and simulator. Native
Shiny targets/references are selected for physical-host builds without making
simulator-only restore/build require Apple or Windows workloads. Select the
appropriate target set before restore, not merely a framework after an
all-target restore. This must be demonstrated before claiming portable builds;
keep profile-specific restore/intermediate/publish paths isolated to prevent
Stock/Simulator project.assets.json or generated-output collisions. Use the
project map in the accepted design; only selected artifacts ship.

### Independent simulator endpoint

Retain the existing UTF-8 JSONL schemas/framing and native engine, but use an
explicit IPv4 loopback TCP listener implemented with existing standard-library
facilities. No new package or async networking framework is required. Bind
only `127.0.0.1`; no wildcard listener, remote hosting, discovery or TLS/auth
subsystem is introduced. Loopback is not caller authentication: arbitrary local
code is outside the trust guarantee, just as for the loopback admin UI.

The server accepts one controller connection and rejects another without
replacing it. Bound frames to 4 KiB and pending adapter requests to eight.
Connection failure invalidates its queued work/generation and zeros the native
engine before admitting another controller; the process stays alive. Replies
and delayed work from an old connection cannot affect a new one. Fault controls
remain local operator/test-only and are not exposed through the guest API.

Remove adapter process-launch/job/kill code and the remote `shutdown` command;
the process starter owns termination. Dispose only the socket/readers. Use
native `stop` for explicit Stop, preserving the currently active tuple; use
native `write` at zero for intentional stopped-envelope changes. The private
dispatch context must distinguish those intentions without adding a public
Stop method to IOssmDevice. Retire stdio mode at coordinated cutover rather
than maintaining two lifecycle contracts.

## Physical device selection and connection

### Discovery and selection

The owning CLI shares one bounded `IDeviceProvider.GetAvailableDevicesAsync` service-advertising
scan for `devices list`, `devices get` when native resolution needs it, and
`prompt --bluetooth` name resolution. Scan for service
`522b443a-4f53-534d-0001-420badbabe69`, post-filter advertisements,
deduplicate by platform ID and return at most 64 candidates in five seconds.
The scan does not connect, issue writes or authorize motion. `devices get <id>`
requires an explicit ID, not a selector or name. Prompt resolves an exact ID
first or one exact unambiguous name; no match or duplicate names fail without
connecting, and the resulting ID is fixed for the session. A platform ID is
opaque and OS-specific; names, signal strength and scan order do not substitute
another device. Discovery returns an empty name when neither advertisement nor
peripheral supplies one; only list presentation may display `Unnamed device`
as a fallback. Get omits an unavailable name rather than inventing one.
Read-only details disclose the device-reported Wi-Fi MAC without enrolling or
verifying it against separate configuration.

The [pinned firmware evidence](spec.md#device-profile-identity-and-feedback)
defines the pairing read as `MAC;chip;wifiConnected;md5;version`. Its MAC is the
Wi-Fi MAC, not a Bluetooth MAC, and is informational rather than authorization
or attestation. An advertised name is mutable and the firmware's Device
Information System ID is not unique.

### Normal startup

Resolve the selected locator through Shiny's known-peripheral lookup where
available. A cache miss is not permission to choose another device: perform the
same bounded service scan and match the exact selected locator. Verify that the
connected peripheral and profile still carry that ID. Discover the required
service/characteristics and validate bounded identity/pattern payloads, supported
firmware and Simple Stroke capability; no expected Wi-Fi MAC is required.
Do not mistake successful identification for physical qualification.

Subscribe to lifecycle/failure events before connecting. Own one attempt and
disable automatic reconnect. Publish connection and validated state through the
existing device contract. Controller initialization owns readiness and required
zero/knob-limit setup under DC-01; discovery alone must not perform navigation,
calibration or motion. On failure, release only settled resources, expose the
failure through the specified lifecycle/exception paths, and do not retry or
restore prior intent automatically.
Read-only identification and normal connections must await bounded native
disconnect cleanup. A disconnect timeout or failure is unresolved I/O, not a
successful cleanup or permission to admit another writer within the same
runtime.

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

For the pinned firmware, knob limiting is enabled by writing ASCII `true` to
`1010`, then reading canonical `true` from its live configuration read handler.
Do not infer success from the write response alone. The
[configuration handler][firmware-knob-config] supplies this protocol; it is not
a state-feedback heartbeat.

The [state characteristic][firmware-state-characteristic] has no live read or
subscription callback. The [notification loop][firmware-notification-loop]
clears its fingerprint on connection-count changes and can then notify a fresh
complete state, but that notification can precede client subscription.
Subscribing later does not replay it. Missing initial feedback must therefore
fail readiness within its bound; reading the cached characteristic must not
manufacture a new sample. The same loop does not notify unchanged state on its
one-second timer, so sustained-motion freshness remains a real compatibility
limit, not a reason to renew deadlines.

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

The web motion profile remains Simple Stroke with sensation `50`. The CLI accepts
pattern explicitly but rejects patterns the selected provider does not implement;
advertised capabilities must remain truthful. Physical qualification is separate.
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
| The current shared dispatch hook receives the service-lifetime token, not a controller check before each GATT write. | Move mechanical revocation/generation checks to the single Runtime I/O owner; web execution adds its current authority guard. Every provider write checks both where applicable. Keep IOssmDevice's ordinary consumer contract unchanged; prove inter-write Stop and settlement before enabling motion. |
| Current simulator dispatch sends every zero-speed request as native `stop`, dropping stopped-envelope changes. | Decision: private explicit Stop maps to native `stop`; intentional stopped configuration maps to native `write` with speed zero. Verify both semantics and never restore an overridden tuple as part of Stop. |
| Stock `1.0.34` has change-only cached state and no effective periodic heartbeat.                                                                                                   | Sustained motion may not satisfy DC-07 freshness even with working BLE. Establish adequate feedback evidence or seek an explicit scope/requirement decision; do not freshen cached reads, lengthen deadlines to conceal failure, or claim the simulator proves compatibility. Physical release remains blocked.        |

The platform build experiment must also prove simulator-only restore/run without
native BLE workloads or permissions. A `macos26.0` package target denotes an API
target; it does not by itself establish a minimum deployed macOS version. Use an
owned CoreBluetooth dispatch queue appropriate to the service host and verify
its lifetime rather than assume a GUI event loop. None of these gates calls for
another BLE process.

## Security and operational behavior

An exact, frozen platform locator prevents accidental substitution, not
impersonation. The reported Wi-Fi MAC and version are not authentication or
attestation; stock firmware does not
supply an authenticated exclusive controller lease. Keep the existing
supervised-use and independent-stopping qualification boundaries.

Treat advertisements, characteristic values and simulator output as untrusted
bounded inputs. Keep device identifiers and raw payloads out of routine logs and
guest projections. Use structured ILogger diagnostics configured through
host-neutral Defaults for mode, connection phases, semantic failures and server loss,
without creating another logging subsystem or duplicating lifecycle failures.
Native Bluetooth access belongs to the owning host identity, not either web
frontend. The simulator adds only the selected loopback listener; no remote
device-control service or arbitrary network exposure is authorized.

## Delivery and verification

The [direct-device migration plan](../../plans/direct-device-migration.md) is
the authoritative task list. Its relevant integration sequence is:

1. Integrate the device contract with direct CLI/web composition and
   one hosted-instance lifetime. Remove superseded types, namespaces, imports,
   project references, registrations, fixtures and documentation in the same
   integration change. Do not leave aliases or a selectable second adapter path.
  Retain the valid `ossm-sim-bridge` executable name and bounded JSONL payload
  meanings while replacing its stdio transport/lifetime.
2. Demonstrate the application through `SimulatedDevice`, including stopped
   configuration, delayed completion, external changes, frozen feedback and
  server failure. Keep fault injection isolated from the guest contract.
3. Verify the intended Shiny host targets and provider corrections before wiring
   physical motion. Add selected-locator validation and read-only
   identity/discovery first.
4. Add serialized stock writes and ordered state handling with integrated
   revocation/settlement checks. Only separately authorized qualification can
   enable a physical profile.

Acceptance evidence extends the existing
[conformance requirements](spec.md#software-timing-and-conformance):

- DI tests prove reference equality of the hosted device and `IOssmDevice`, one
  selected owner, pre-start event subscriptions and shutdown ordering.
- Composition tests prove stock mode has no simulator/Cargo dependency and
  simulator mode neither initializes Bluetooth nor needs BLE identity options;
  inspect transitive restore/publish output, not only DI registrations.
- Independently started simulator tests prove second-client rejection, zero on
  lost connection, no old-generation writes and process survival after CLI/API
  disposal.
- Common contract/controller tests use the same typed states and operations for
  both implementations. Deterministic fakes prove policy and timing; native
  simulator integration proves the Rust boundary, not BLE semantics.
- Selection tests cover duplicate names, absent/wrong locators, unsupported
  firmware, malformed/oversized payloads, missing GATT surface, cache misses,
  invalid selected ID and no fallback or automatic reconnect. A different valid
  reported Wi-Fi MAC must not block an otherwise valid selected connection.
- Controller-to-adapter tests revoke authority or latch Stop between individual
  writes, then deliver late completion. Only the permitted zero operation may
  follow; no replacement connection or writer is admitted prematurely.
- Provider tests cover real platform lifecycle, notification errors, overload,
  cached-value provenance and native settlement. Fakes cannot certify these.
- Repository checks find no superseded adapter references in maintained source,
  tests, project/configuration files or documentation. Update architecture views
  with implemented project/ownership changes, without retaining obsolete paths.

This documentation change does not run those checks or implement that removal.
Configuration, process ownership and private Stop/configuration decisions are
specified above; their implementation still needs the acceptance evidence. Physical
support then requires evidence against the existing
[qualification gates](spec.md#physical-qualification).

## Alternatives considered

| Alternative                                      | Reason not selected                                                                                                                                                |
| ------------------------------------------------ | ------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| Rust BLE helper                                  | Adds a process and protocol boundary for operations Shiny can perform in-process; conflicts with the accepted deployment direction.                                |
| Embed the simulator through a C ABI | Removes a process but adds native ABI, memory/error ownership and runtime-lifecycle work. The independently runnable bounded endpoint meets the accepted scope. |
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
[firmware-knob-config]:
  https://github.com/KinkyMakers/OSSM-hardware/blob/4871a84c54ae0734b1f8bc3d02fcc0098f12fbad/Software/src/services/communication/config.hpp
[firmware-state-characteristic]:
  https://github.com/KinkyMakers/OSSM-hardware/blob/4871a84c54ae0734b1f8bc3d02fcc0098f12fbad/Software/src/services/communication/state.hpp
[firmware-notification-loop]:
  https://github.com/KinkyMakers/OSSM-hardware/blob/4871a84c54ae0734b1f8bc3d02fcc0098f12fbad/Software/src/services/communication/nimble.cpp
