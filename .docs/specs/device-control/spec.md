# Spec: Service-owned device control

Module: `device-control`

Status: Revised draft for review, updated for the approved SignalR migration on
16 September 2026. ControlService owns session authority, safety policy,
ControlCore and device execution. AdminWeb and GuestWeb are separately hostable
server-side Blazor clients. Stable DC requirement IDs are retained; review this
revision before Plan. Hardware qualification is not approved.

The user approved the local simulator-first ControlCore implementation on 14
September 2026. Its [implementation contract](../control-core.md) uses the
existing OSSM-RS motion engine through a headless adapter. This is a separate
`ossm-rs-sim-v1` profile, not qualification of the stock profile below. Current
repository instructions select xUnit v3, MTP v2, FastMoq where appropriate, and
memory-efficient .NET buffer handling.

## Objective

Provide one reliable .NET 10 controller running only in ControlService. The
client uses a single browser, remotely or on the same computer, that talks only
to GuestWeb. AdminWeb, GuestWeb and the CLI call the versioned SignalR
ControlService; ControlService authenticates hosts and independently authorizes
input before the controller communicates with OSSM through a replaceable
server-side .NET device bridge. Neither a browser nor a Blazor component owns
the device connection.

Success means initialization cannot expose premature readiness, concurrent
inputs cannot interleave motion transitions, Stop invalidates stale work, and
failures never appear as confirmed physical outcomes. This module owns device
execution; authentication/session handling and permission policy remain separate
modules. The guest web process has no controller or bridge.

The first deliverable uses a simulated device bridge. Physical input stays
disabled until qualification. Server enforcement removes dependence on
guest-side validation, but does not itself prove physical safety.

## Resolved design decisions

| Decision                | Selected approach and reason                                                                                                                                                                                                                                                                             |
| ----------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Application stack       | ControlService plus separately hostable ASP.NET Core/Blazor AdminWeb and GuestWeb clients. ControlService owns the device-control hosted service. They can run on one computer.                                                                                                                           |
| Access model            | Deliberately unauthenticated AdminWeb bound only to `127.0.0.1`, with Host/Origin protection and no access timeout. Generated distinct AdminWeb/GuestWeb service keys authenticate hosts to ControlService; guest invitation/session capabilities remain independently validated there. No registration, login, user database or identity provider. |
| Licensing               | Plan Apache-2.0 licensing for original application code. Existing web projects are behavior/protocol references, not source imports or mechanical ports. No third-party branding/assets or licence files are installed by this document.                                                                 |
| Device bridge           | Map the pinned stock protocol in a server-side .NET bridge behind a narrow C# interface. Native BLE is the initial transport direction; the actual home-server OS and service-account Bluetooth access must be verified before selecting its implementation. No browser BLE or JavaScript device bridge. |
| Initial device target   | Qualify stock firmware `1.0.34` at `4871a84c54ae0734b1f8bc3d02fcc0098f12fbad`, on an operator-identified OSSM configuration. This selects a test target, not support for all units reporting that version.                                                                                               |
| Initial client platform | Current desktop Chrome over HTTPS, without device permission, extension or local installation. [Blazor supports Chrome][blazor-platforms]. Browser compatibility is independent of the home service's native BLE platform.                                                                               |
| Initial motion profile  | Simple Stroke only: firmware pattern ID `0`, fixed sensation `50`, no inversion. Speed and travel controls remain subject to local limits and a qualified geometry mapping. Other reported patterns are visible as unsupported, not automatically enabled.                                               |
| Test tooling            | xUnit v3 with Microsoft.Testing.Platform v2, FastMoq where appropriate, server-side `TimeProvider`/`FakeTimeProvider`, application-boundary integration tests and guest UI smoke. ControlCore dependencies are approved; no Node/Vitest toolchain is selected.                                           |
| Physical release        | Keep physical remote input disabled until the release evidence below exists. Research cannot choose a safe stopping bound for an unspecified loaded machine. Software development proceeds against the simulator without treating a server timer as independent device protection.                       |

This retains .NET 10, stable module IDs and the stock-firmware constraint. It
removes the extra host browser, WebAssembly controller, browser device adapter,
named-pipe control path and old REST control surface. ControlService is the
control host.

### Service boundary and division of responsibilities

Use the
[separate-services runtime](../README.md#separate-guest-and-admin-services).
AdminWeb, GuestWeb and ControlService are separate processes/listeners, not
route groups. Only ControlService loads the bridge and owns its native
permissions.

Support [local one-box operation](../README.md#dep-01-local-one-box-operation):
both services, bridge and browser can share one supported computer without cloud
infrastructure. The same server checks apply locally and remotely.
Unauthenticated operator management runs only in loopback AdminWeb, with
Host/Origin protection. It is not a security boundary against local processes.

The session module owns
[automatic reusable invitations](../README.md#session-01-automatic-reusable-invitation).
Retrieving the automatically available invitation is an admin operation
unavailable to guest web, not device initialization, guest approval or arming.
It must issue no bridge writes and must not alter an active controller's
generation or motion state. The invitation has no expiry and can admit a new
pending guest after the previous session ends; guest/session leases still
expire. **Cycle link** is a separate admin operation: revoke old
link/proof/connection/grants, clear pending input and request serialized Stop.
The replacement is available immediately, but does not clear an external lock,
approve a guest or arm/resume control. The operator's **Stop and end session**
action uses this same atomic cycle. Guest leave, transport loss and
guest/session expiry retain the invitation.

The [accountless access contract](../README.md#session-02-accountless-access)
defines unauthenticated local operator access and scoped guest capabilities.
Guest authentication validates temporary authority, not an account. The
controller receives only trusted scope/session context; no guest token becomes
an execution guard by itself.

| Boundary                         | Owns and enforces                                                                                                             | Must not do                                                                                       |
| -------------------------------- | ----------------------------------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------- |
| Guest browser                    | Displays three vertical sliders, explicit/automatic Send, current feedback and Stop                                           | Authorize itself, select devices, raise limits, connect to the bridge or emit raw device commands |
| GuestWeb                         | Rendering, bounded parsing/quotas and guest hub commands; its generated service key proves the host only                    | Generate invitations, approve, arm, change policy or open the bridge                              |
| ControlService session boundary  | Independently validates guest capability and session, atomically handles invitations/admission, issues grants and owns expiry | Trust GuestWeb's allow/deny result or a client-claimed role                                       |
| `host-authority` in ControlService | Operator permissions, arming, configured limits, valid tuples, grant/sequence checks and revocation                         | Delegate final policy to GuestWeb                                                                  |
| `device-control` in ControlService | Single writer, current generation, execution-time policy checks, device readiness, deadlines and Stop ordering              | Create a writer in a web host, CLI or per hub call                                                |
| .NET bridge                      | Private device I/O, stock protocol mapping, bounded completion and device events                                              | Expose a guest endpoint, bypass the controller or retain a replay backlog                         |

Use server-side Blazor components in GuestWeb for rendering. Every guest entry
point uses ControlService's limited SignalR contract, with both GuestWeb service
authentication and the guest proof validated by ControlService. UI code and web
hosts have no bridge reference. ControlService owns controller lifetime;
reconnection or rerendering cannot create a new writer or restore authority.

The public guest contract terminates at GuestWeb and is served through the
versioned, independently validated ControlService hub at `/hubs/control/v1`.
SignalR is the only control transport; application operations are only
future-REST-compatible, not REST endpoints now. There is no host browser,
operator credential or guest-to-device peer channel. An authenticated session
still cannot call arbitrary bridge methods or address devices by client-supplied
identifiers.

Blazor's retained component state is not evidence that the guest remains
connected. ControlCore uses Send/Auto send, not hold-to-run. Only a newly
received, validated run input with the current sequence and unexpired
server-issued grant can authorize continued motion. A server timer must never
resend the last UI value to renew its own authority. The visible browser renews
only the last explicitly sent tuple; unsent selections never enter that run
lease. Visibility JavaScript stops sending, but no browser code is trusted to
enforce access or safety policy.

Operator approval, configuration and rearming are trusted server operations,
separate from guest requests. ControlCore starts disarmed with 100% limits; the
explicit Allow control action prepares and arms the stopped session. Device
pairing and calibration are operator setup actions, never guest browser prompts.

ControlService's watchdog runs even when the guest browser or GuestWeb is closed
or frozen. ControlService/OS/bridge failure is a separate fault boundary
requiring independent physical protection. Do not claim ASP.NET graceful
shutdown runs after a crash or that a server-side timer is a hardware interlock.

### Ideas adopted from existing projects

The comparison set is XToys, Sexync/SexCode and OSSM Possum, as named in the
handoff. These are design references, not dependencies or endorsements.

| Reference                                                           | Adopt                                                                                              | Do not inherit                                                                                                             |
| ------------------------------------------------------------------- | -------------------------------------------------------------------------------------------------- | -------------------------------------------------------------------------------------------------------------------------- |
| [XToys admission][xtoys-admission] and [owner limits][xtoys-limits] | Separate permission to join from permission to control; keep ceilings and device setup owner-only  | Its replacement firmware, room-code authorization assumptions or a claimed independent stop guarantee                      |
| [SexCode protocol][sexcode-protocol] and [schema][sexcode-schema]   | Explicit handshake, advertised capabilities, distinct stop/reset semantics and versioned contracts | Its binary protocol or replacement firmware; its 250 ms schema watchdog default is not a stock-BLE physical stopping bound |
| [Sexync product][sexync-about]                                      | Private, one-to-one admission and clear revocation                                                 | Accounts, identity-verification workflow, media/chat, hosted-platform dependencies or automatic reconnection semantics     |
| [OSSM Possum][possum] at `ebd01fcaab8c99b926ce7dd58a2cec2a879a29f7` | Compact local controls, device-reported pattern IDs and an explicit disconnected state             | UI-owned BLE operations, remembered resume speed, native-platform assumptions, or disabling the physical knob limit        |

Possum is TypeScript/Svelte with Capacitor, not a separate native device
controller. Its implementation is useful evidence that a small control surface
is sufficient, but not a reason to import its framework or its coupled
controller. Our stock adapter remains independent of the UI.

SexCode implements watchdog behavior in replacement device firmware. That is the
useful architectural distinction: a protection that must survive host failure
belongs outside the host. Neither its timings nor Sexync's product claims
establish that protection on stock OSSM firmware.

## Source baseline and evidence

These are immutable inspection references, not installed dependencies.

| Component                           | Inspection reference                                                                                  | What is established                                                                                                       |
| ----------------------------------- | ----------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------- |
| Reference application, not imported | [OSSM-Web-Control][app], commit `e65032ba2ed9c5709d91e5afd4f7034d7965fa32`                            | TypeScript/DOM application; its licence is GPL-3.0                                                                        |
| Bundled BLE client                  | [`ossmBle.js`][bundle] at the application commit; Git blob `faf64bf115ada09b19a0cee3546dbd3c7c2f1271` | Declares library version `1.1.2`; bundled licence is Apache-2.0                                                           |
| Bundled declarations                | [`ossmBle.d.ts`][declarations]; Git blob `05538335fea0cc75c896230a622aedfe695bbd52`                   | Public methods and declared parameter semantics                                                                           |
| Reference BLE source, not imported  | [OSSM-BLE-Web][ble-source], commit `d2a9de6cb8fbdeaba5a2d4345c69d7a70d4789f2`                         | Prior browser behavior and mapping evidence, not a .NET build input                                                       |
| Firmware qualification target       | [OSSM-hardware][firmware], tag `1.0.34`, commit `4871a84c54ae0734b1f8bc3d02fcc0098f12fbad`            | [Version header][firmware-version] confirms the tag's version; installed firmware and physical behavior remain unverified |

The following source patterns were found. None has been reproduced by an
executable regression test in this repository yet.

| Handoff finding                    | Pinned source evidence                                                                                                                                                           | Required proof                                                                                                              |
| ---------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | --------------------------------------------------------------------------------------------------------------------------- |
| Knob ceiling disabled              | [`script.ts`, lines 1191-1202][knob-call] calls `setSpeedKnobConfig(false)`; declarations describe `true` as proportional knob limiting                                          | Initialization requires `true` readback; configuration failure prevents readiness; verify actual scaling on hardware        |
| Interleaved transitions            | [`script.ts`, lines 1093-1189][play-input] allows overlapping input handlers; bundled `runStrokeEnginePattern` performs multiple awaited calls using shared `pendingStateTarget` | Concurrent inputs and cancellation cannot interleave whole transitions or restore revoked motion                            |
| Navigation not awaited             | [`script.ts`, lines 797-835][navigation] starts and logs `navigationPromise` without awaiting it                                                                                 | Navigation rejection cannot expose controls or issue subsequent nonzero commands                                            |
| Stable-state failure falls through | [`script.ts`, lines 782-795][initial-state] catches the failure without aborting initialization                                                                                  | Failure latches fault; late initialization results cannot publish ready                                                     |
| Listener identity mismatch         | [`script.ts`, lines 612-622][cleanup] removes fresh bound callbacks rather than those registered at lines 894-896; the bundle also binds native callbacks during setup           | Repeated connection lifecycles leave one owned subscription while active and none after disposal                            |
| Recursive disposal                 | [`styles.ts`, lines 103-145][slider-disposal] has `removeInstance()` call `dispose()`, which calls `removeInstance()` before removal                                             | Disposal terminates, removes the registry entry once and releases observers/listeners, including after partial construction |

These findings are acceptance examples for the new C# controller, not tasks to
patch discarded TypeScript code. The recursive range-widget defect and the old
library's implementation are not inherited. Tests must prove the same
initialization, ordering, callback and cleanup invariants through the new
contracts; actually retained defects still require failing-then-passing
regression proof.

Two source details constrain the design:

- The bundled `stop()` clears its internal queue, writes speed zero and resolves
  without confirming stopped feedback. When not ready, it can return without a
  write. Its queue reset does not establish that an already-issued GATT
  operation has finished.
- The selected firmware's [disconnect handling][firmware-disconnect] requests
  the menu when the last connection disappears and contains a separate ramp-down
  path with a one-second delay and two-second ramp. These code paths do not
  prove an end-to-end stop bound, behavior before BLE detects loss, or behavior
  while another client remains connected.

### Device profile, identity and feedback

Use the versioned profile ID `ossm-stock-1.0.34-simple-v1`. The simulator models
that contract; the physical adapter starts unqualified. Contract fakes exercise
both capable and incapable variants, including the stock profile's documented
limitations. A passing capable fake must never upgrade the physical profile's
capability flags.

The inspected [Device Information service][firmware-disconnect] exposes
manufacturer/system information but no firmware-revision characteristic. Do not
invent an automatic version probe. An unloaded qualification record must
identify the installed build from its trusted installation record and record
hardware configuration and calibrated travel. Local selection of that recorded
profile is an operator assertion, not cryptographic device attestation. Unknown
installation history cannot qualify remote arming. No guest field or ordinary UI
toggle may mark a profile qualified.

The [firmware state implementation][firmware-state] reports more than the older
BLE declarations: `timestamp`, `state`, `speed`, `stroke`, `sensation`, `depth`,
`buffer`, `pattern`, `position` and `sessionId`. Give this exact stock profile
its own parser. Normalize only required control/feedback fields into the public
device contract; never forward firmware session identifiers or raw telemetry
into the remote protocol.

Do not infer physical Stop from this payload. `speed` is an integer conversion
of the effective setting, not the original BLE request or a motor-stopped flag;
`position` is based on the stepper counter. The separate [play-control
loop][firmware-play] multiplies the physical knob by the BLE speed percentage.
Thus reported speed zero, including at knob zero, is not evidence that the
stored BLE request is zero.

Always issue an explicit zero-speed command for Stop. Do not suppress it, or any
changed requested speed, by comparing it with effective-speed feedback. For
example, a previous BLE request of 100 and knob at 50 can report 50; a new
request of 50 still requires a write.

There is also a source/documentation mismatch: the [BLE
documentation][firmware-protocol] describes periodic notifications, while the
inspected loop updates and notifies the state characteristic only when its
fingerprint changes. Successful reads can therefore return unchanged stored data
or an `ok:<command>` processing response. Model those as distinct protocol
variants; neither renews device-state freshness without evidence of a new device
sample. Never toggle motion parameters to manufacture freshness.

The initial physical profile therefore does not advertise confirmed-stop,
continuous fresh-state or independent-watchdog capabilities. It remains
ineligible for physical remote control unless qualification supplies those
guarantees without changing firmware or relaxing the contract. If stock firmware
cannot supply them, stop at simulator/unloaded work and revisit scope
explicitly; do not keep increasing timeouts until arming succeeds.

### Initial parameter mapping

For the Simple Stroke profile, the desired tuple uses integer firmware
percentages in `[0, 100]`, pattern `0` and fixed sensation `50`. Nonzero speed
requires positive stroke. The simulator-first ControlCore implementation
defaults to a 100% software speed cap and maximum depth; operators may lower
either limit. Physical-device operation remains blocked until separately
qualified.

[Simple Stroke][firmware-patterns] targets depth and depth minus stroke.
However, the [mode mapping][firmware-mode] scales percentages against measured
travel, and the [engine][firmware-engine] clips depth and stroke against travel
minus physical keepout margins. Consequently the nominal percentage interval
alone is not a verified physical envelope.

The profile owns that calibrated conversion and its rounding rules. Reject a
requested or intermediate tuple if it cannot be proven inside the local envelope
without relying on firmware clipping. Do not derive millimetres from a
percentage without the qualification record.

Use exact field values for this profile, subject to adapter qualification; do
not inherit the BLE library's unconditional interior-value decrement. The
selected [firmware command handling][firmware-state] assigns the received
values, while the older library subtracts one for stroke, depth and sensation.
Verify boundary and interior round trips before enabling those writes on
hardware; any required correction belongs to a separately qualified mapping, not
a global undocumented offset.

## Tech stack

Use .NET 10/C# with nullable reference types enabled and portable
controller/contract code targeting `net10.0`. Use AdminWeb and GuestWeb as
server-side Blazor [Interactive Server][blazor-rendering] hosts, calling
ControlService's SignalR endpoint `/hubs/control/v1`. No WebAssembly
device-control runtime is required. Never host ControlService management in
GuestWeb.

Register one ControlService-owned controller/bridge instance for the device and manage
its execution with [IHostedService/BackgroundService][hosted-service]. The
ControlService hub validates guest requests and passes immutable
capability context to that owner. Do not capture scoped connection services
inside a singleton or treat a circuit-scoped controller as exclusive device
ownership. Background timers must not depend on an active rendering circuit.

The bridge is a .NET adapter, initially in-process with ControlService only. Keep
platform-specific BLE handles, OS APIs and protocol details behind it. For
native BLE the ControlService host needs access to a Bluetooth adapter; hosting code
alone does not provide that hardware. Select the OS adapter, service identity,
permissions and provisioning flow against the actual home deployment, not the
development machine.

AdminWeb and GuestWeb use generated distinct service keys over HTTPS/WSS to
authenticate to ControlService. Those keys are distinct from operator authority
and guest invitation/session capabilities; ControlService validates the latter
on every guest operation. GuestWeb has no device permission or access to
ControlService bootstrap/secret storage. A hub connection loss cancels its
sessions; ControlService's existing guest-grant deadline covers undetected
stalls. Guest reconnect and ControlService restart do not restore authority.

If the bridge later moves out of ControlService's process, define a private
authenticated, versioned protocol with session/generation fencing and no replay
before enabling it. Do not silently create a public bridge endpoint. Such a
split is an extension point, not an additional first-release service.

Select xUnit v3's explicit `xunit.v3.mtp-v2` integration with the .NET 10
[Microsoft.Testing.Platform runner][dotnet-test], using MTP v2. Use FastMoq for
useful constructor/collaborator wiring, not as a replacement for deterministic
device and clock fakes. Use framework [TimeProvider][time-provider]
timestamp/elapsed-time APIs for monotonic decisions, with
[FakeTimeProvider][fake-time] in tests. Verify suspend/resume behavior on the
service host. Do not add a parallel custom clock abstraction or trust
guest-supplied timestamps.

Use the
[functionality/package inventory](../README.md#functionality-and-package-inventory)
as the authoritative planned dependency list, including System.CommandLine for
local admin CLI, schema validation and test providers. It distinguishes
framework APIs from NuGet references and pins the proposed SDK/runtime. The
implementation plan must verify the resolved graph before installation; this
spec installs no packages, SDK, browser driver or device adapter.

## Commands

There is no local application manifest or runnable test suite yet. These are the
selected command contracts to establish during implementation, not commands
already present or verified in this checkout:

| Command                                                                      | Status and purpose                                                                                                                |
| ---------------------------------------------------------------------------- | --------------------------------------------------------------------------------------------------------------------------------- |
| `dotnet build`                                                               | Build ControlService, AdminWeb, GuestWeb, hub contract, controller and bridge with nullable checking and analyzers                |
| `dotnet test --project tests\DeviceControl.Tests\DeviceControl.Tests.csproj` | Focused portable controller/protocol suite using the .NET 10 MTP runner; must discover a nonzero test set                         |
| `dotnet run --project src\ControlService\ControlService.csproj`              | Run the SignalR owner of device-control; simulator bridge by default, no physical motion on startup                               |
| `dotnet run --project src\AdminWeb\AdminWeb.csproj`                          | Run loopback-only, deliberately unauthenticated operator Blazor host; it calls ControlService                                    |
| `dotnet run --project src\GuestWeb\GuestWeb.csproj`                          | Run restricted Blazor guest host using ControlService's hub; unavailable service disables control and never falls back to local device execution |

The implementation plan must make these commands real, select MTP in
`global.json`, and keep portable tests runnable without a browser/Bluetooth.
ControlCore's implemented commands and actual project names are documented in
the repository README; this table describes the broader feature contract.

## Project structure and ownership

These are proposed placements, not separate packages:

| Placement                      | Owns                                                                                                           |
| ------------------------------ | -------------------------------------------------------------------------------------------------------------- |
| `src\DeviceControl\`           | Portable C# contracts, lifecycle, desired-state scheduling and single-writer execution                         |
| `src\DeviceBridge\`            | .NET bridge implementation and stock protocol mapping; native device I/O behind the controller-owned interface |
| `src\ControlService\`          | SignalR hub, authoritative sessions/policy and hosted device-control composition                               |
| `src\AdminWeb\`                | Loopback-only, deliberately unauthenticated server-side Blazor operator host                                   |
| `src\GuestWeb\`                | Restricted server-side Blazor guest host; no management/controller/bridge reference                             |
| `src\SessionContracts\`        | Closed versioned hub messages and explicit operation allowlists                                                 |
| `tests\DeviceControl.Tests\`   | Portable controller/protocol contract tests with fake GATT boundary and FakeTimeProvider                       |
| `tests\ServiceBoundary.Tests\` | Hub authentication, operation restrictions, ControlService lifecycle, transport loss and guest UI smoke        |
| This specification             | Required behavior, source evidence and qualification gates                                                     |

Only the bridge depends on native device handles/transport APIs. Controller
contracts do not depend on ASP.NET Core, Blazor or WebSocket implementation
types. Guest web references only the limited contract/client; it cannot resolve
controller, management or bridge services through DI. A simulator implements the
same device boundary without production simulation branches.

Do not publish raw `sendCommand`, `batchSet`, arbitrary method invocation,
position streaming or firmware operations through the controller.

## Required behavior and interface contracts

### DC-01: Explicit lifecycle and readiness

Use distinct disconnected, initializing, ready, paused, locked, faulted and
disposed states. These are server controller states, not the remote session
state machine. Only ready accepts nonzero motion. The external-control lockout
in DC-09 remains latched even when a fault or disconnect must also be reported;
initialization or normal fault recovery cannot silently clear it.

Device selection and pairing are trusted service/operator setup. An ordinary
guest cannot change the configured target or pair another device. Server
readiness and device readiness are separate: the website may serve status while
the device is disconnected or faulted.

Device initialization must establish known zero output, enable and read back
knob limiting, validate current state and capabilities, await any required
navigation, and obtain fresh readiness evidence before publishing ready. Every
asynchronous step has a finite deadline and checks current connection identity
and generation.

Navigation that can home or calibrate requires explicit local consent; it must
not occur as an implicit recovery from an unknown device state. No initializing
or preflight status is treated as ready solely because it belongs to the
stroke-engine page.

Initialization failure enters fault, disables motion and clears pending work. A
zero/stop attempt is still allowed when the transport permits it; no subsequent
nonzero write or ready publication is allowed. Successful recovery requires a
fresh, explicit local initialization at zero.

### DC-02: Narrow consumer surface

The following operations define responsibilities; final language-level names and
types belong in the approved technical plan.

| Operation               | Contract                                                                                                                                                                      |
| ----------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Initialize device       | Hosted controller acquires the configured device through its .NET bridge after trusted operator setup; produce ready-at-zero or an explicit failure                           |
| Submit desired state    | Accept a complete tuple and a required trusted in-process execution guard; return a request revision and eventual applied, superseded, rejected, cancelled or faulted outcome |
| Request Stop            | Synchronously revoke pending motion and latch pause without clearing an existing lockout/fault; independently report the stop outcome                                         |
| Resume/recover          | Require trusted owner action at the service boundary, never an ordinary guest request; preserve zero until a new valid request; full initialization after fault               |
| Unlock external control | ControlService operator operation only; require the expected current authority revision, satisfy DC-09's recovery checks, clear the latch and remain paused at zero; never arm or resume motion |
| Observe state           | Provide immutable state through the one ordered ControlService initial/live push path; distinguish requested, accepted and device-reported values                              |
| Dispose                 | Revoke authority immediately, attempt bounded stop/cleanup, release ownership once and expose any unconfirmed stop outcome                                                    |

Request acceptance is not a completed transition. Transition completion is not a
measured mechanical result. Hub command replies are acknowledgement-only; every
accepted request gets one terminal software outcome, including superseded or
cancelled work. Join returns only private bootstrap admission proof. Initial and
live state are delivered on one ordered push path, not command replies.

Inside the service process, the execution guard exposes a synchronous allow/deny
decision under the current policy and freshness rules. It is mandatory, cannot
originate from guest payloads and cannot perform asynchronous work. Trusted
operator control supplies its eligibility guard; `host-authority` supplies its
permission/epoch/grant check without exposing wire schemas here. The controller
also checks its own generation and device invariants. A guard error faults the
controller, not just the request; it never defaults to permission.

Retain the guard associated with currently applied nonzero output. The
expiry-check timer evaluates it even when no new request or write is pending.
Revocation latches Stop without waiting for another input, while the service can
execute. Request completion does not extend or detach motion authority; this is
not a guarantee of physical stopping.

Applying compatible operator limits does not revoke this accepted guard or pause
control. Preserve Ready, generation, epoch, sequence, approved session and
serialized I/O when the accepted tuple, active observed tuple and issued work
fit the proposed bounds. A successful changed policy increments its version and
immediately publishes a grant with that version and the same epoch; old grants
cannot authorize new input. Retain the accepted guard's original deadline, not
the new grant's deadline. Only explicit fresh guest input can renew the accepted
lease. Applying identical limits is a no-op. Paused, initializing, locked and
faulted states never arm from policy edits; Stop and expired authority remain
dominant. Any operator action that restores authority, including approval,
arming and unlocking, supplies the expected authority revision. Stop advances
that revision and invalidates an in-flight restore that captured an older value.

### DC-03: Capability and value contract

Capability information identifies the adapter contract and verified firmware
profile, known patterns, field units and resolution, valid combinations,
readiness evidence, available feedback and causal parameter attribution.
Observed device features are separate from features qualified for use. Unknown
or unqualified capabilities cannot enable motion.

`CausalParameterAttribution` is required before a profile may be qualified for
physical remote control. For each parameter-setting observation, it supplies a
connection-scoped monotonic sample identifier, monotonic observation time,
freshness source (`notification`, explicitly correlated readback, cached read
or command response), parameter representation and one attribution result:
`verified-owned-write`, `verified-external-change`, `ambiguous` or `not-fresh`.
A `verified-owned-write` identifies the exact connection, controller
generation, transition step and bridge write operation it confirms. Value
equality, arrival time or a broad tolerance alone cannot create that result.

The profile represents raw requested BLE speed and effective knob-scaled speed
separately, including units, resolution, mapping revision and whether each is
observable. When raw requested speed or an independently observed knob state is
unavailable, the bridge reports that uncertainty rather than inferring raw speed
from the effective value. Cached reads and command responses cannot establish a
new parameter baseline or authorize motion.

Use a complete desired tuple covering pattern, speed, stroke, depth and
sensation under the selected capability profile. Pattern identifiers have
explicit profile-specific meaning; no global meaning is inferred from a number
or display name.

Reject wrong types, nonfinite numbers, unsupported patterns, invalid
resolution/ranges and invalid combined geometry. Do not coerce, silently clamp,
or choose a fallback pattern. Upstream percentage fields are not millimetres,
calibrated speed or universally interchangeable units.

Validate the whole travel envelope and every intermediate tuple. The adapter
owns firmware mappings and verified update order; the host owns the user's
permitted envelope. Both constraints apply at execution. Simple Stroke is the
sole candidate pattern. The physical qualified allowlist remains empty until
evidence is recorded; simulator support must not populate it.

Reject a proposed limit change as `limits-conflict` before any mutation if
accepted, active, pending or in-flight motion exceeds it. Include zero-speed
envelope writes and completed commands whose feedback has not yet arrived; write
completion does not erase possible active motion. Conflict rejection preserves
the old limits/version, authority, desired tuple and writer. Do not silently
pause, clamp, accept a forbidden transient or re-arm to apply limits.

### DC-04: One writer and bounded pending work

Route all local and future remote motion, pattern selection, initialization,
maintenance, fault handling and Stop through the same execution owner. Serialize
complete transitions and all GATT operations that can contend with them,
including configuration and request/response reads.

Endpoint, circuit, timer and bridge callbacks can execute concurrently. Give
state mutation, Stop latching and final validation/write issuance one serialized
owner. Order the final check and operation issuance against Stop; acceptance
into an application queue is not device execution. The bridge must not create an
independent queue that bypasses cancellation or generation checks. Never hold a
lock across awaited device I/O.

Keep at most one transition in flight and one replaceable desired-state slot.
The slot contains the tuple, request revision and its execution guard together.
Never combine a tuple from one request with another request's authority.

Check lockout, generation, guard and tuple validity on receipt, after each
await, and immediately before each write. Opaque multi-write helpers may be used
only if they provide that same check at each write. An outer queue around the
existing `runStrokeEnginePattern()` alone does not satisfy this rule.

### DC-05: Stop wins without concurrent writes

On receipt by the server controller, Stop synchronously inhibits motion,
advances generation and clears desired and queued motion. A normal Stop enters
paused; an existing external-control lockout or fault stays latched. Propagate
that invalidation to pending bridge work before permitting another write.
Repeated Stop is idempotent and does not queue a backlog. It requires no valid
motion grant, but a guest Stop still requires an authenticated session. Internal
fault/operator Stop does not depend on guest connectivity. A guest click means
requested until the service acknowledges latching; that acknowledgement is not
physical stop evidence.

An already-issued GATT operation cannot be recalled reliably. Once that
operation settles within its bound, execute the verified stop operation before
ordinary work. Do not reset a queue into allowing a second writer. No old
transition may issue another motion write or publish success.

A transport timeout faults the controller and leaves physical state unknown.
Retain ownership of any unsettled operation; do not treat a rejected timeout
wrapper as proof it finished. Do not issue concurrent stop writes to hide this
condition. Show the physical-stop instruction; if the operation later settles
and the connection remains usable, a serialized stop attempt is allowed, but
motion remains faulted.

A .NET cancellation token or one-second application deadline is not proof that
an issued bridge/GATT operation ended. Track the underlying bridge operation
after logical timeout; no replacement writer may reuse that connection
prematurely.

Stop reporting distinguishes request latched, stop command completed,
device-reported stop evidence, and unconfirmed stop. Only a verified feedback
mapping may produce "device reports stopped". A cached zero setting or
successful BLE write alone is insufficient. Even verified device-reported state
is labelled as feedback, not physical measurement.

### DC-06: Safe transitions and local maintenance

Before changing pattern or travel envelope, command and confirm zero using the
verified profile. Apply the verified intermediate sequence while stopped,
validate the resulting tuple, and restore requested speed only if the current
generation and execution guard still permit it. Any failure stays stopped or
faulted; it never resumes the previous speed.

For a valid Simple Stroke tuple, the nominal sequence is: stop, shrink stroke to
the smaller of current/desired stroke, change depth, then expand stroke to the
desired value. Validate the physical mapping at each step; reject a transition
that requires a different sequence rather than issuing a guessed repair. The
simulator must prove intermediate-envelope containment; the hardware
qualification must verify the same mapping.

Homing/calibration is a local precondition for the first delivery, performed
through the device's existing physical controls. Do not add a remote or UI
calibration implementation to obtain readiness. A future local maintenance
surface requires its own contract and approval. Device switching invalidates and
disposes the old connection before a new controller can accept motion.

### DC-07: Feedback, loss and disposal

Treat bridge/device notifications, readbacks and pattern metadata as untrusted
inputs. Validate shape, state and units before publishing snapshots. Track
freshness using a controllable monotonic clock; wall-clock changes cannot renew
readiness. Missing/invalid required feedback faults the device; observed
parameter changes not explained by owned operations enter the latched
external-control lockout in DC-09. Neither path may repair the device by
replaying previous desired settings.

Authority deadlines use ControlService's TimeProvider timestamps. Guest
release/hide/leave messages invalidate motion when received; detected transport
loss or stale input invokes Stop independently in ControlService. A frozen or
dead guest browser or GuestWeb cannot renew a ControlService-issued motion
grant. Transport/SignalR ping/pong and a live circuit do not renew that grant.
The 1-second and 3-second transport-loss thresholds are tunable detection
settings, not safety guarantees, real-time deadlines or wall-clock clock
synchronization.

On return from service/OS suspension or an overdue expiry check, invalidate
authority before processing buffered work. Check deadlines at request intake and
bridge completion as well as on the timer; do not depend on which callback the
scheduler delivers first.

Guest/device/hub disconnect, disposal and faults cancel pending work. No browser
reload, circuit reconnection, bridge reconnect or ControlService restart
restores authority or replays desired state. New guest sessions may reuse the
current invitation but need fresh proof and trusted owner approval/rearming.
ControlService restart, Cycle or operator Stop and end session invalidates the
previous invitation. Internal end/revocation does not wait for an
acknowledgement from the guest.

On graceful service shutdown, reject new motion, invalidate grants, request
bounded Stop and dispose the bridge. `StopAsync` is not guaranteed to run on
process failure; retain independent physical-stop requirements. The controller
must stay reachable to internal Stop even when the request that submitted motion
has ended.

Store each .NET/native callback identity once. Unsubscribe, cancel timers, stop
notifications and release owned connection resources on success, failure and
partial initialization. Give registry removal one owner. Repeated cleanup is
harmless and late callbacks cannot affect a replacement connection. Cleanup must
preserve visible unconfirmed-stop status rather than replacing it with an
apparent successful disconnect. Blazor circuit disposal and component rerenders
must not destroy or recreate the application-owned controller. They revoke the
relevant session through the application boundary, not by disposing shared I/O.

### DC-08: Replaceability and compatibility

A second adapter must satisfy the same observable contract and conformance suite
without changing controller policy, the guest UI or session consumers. Start
with the stock adapter and a fake, not speculative production alternatives.

Follow the approved map's versioning rules for any serialized capability
profile. Internal C# contracts need explicit variants and units, not a separate
version number for each method. Wire negotiation belongs to `session-relay`;
this module must not invent a competing protocol. An adapter replacement with
different semantics needs an explicit contract/profile change and fresh hardware
qualification.

### DC-09: External parameter changes latch an admin-only lockout

When qualified device feedback shows a control parameter or operating-mode
change that cannot be explained by the controller's own operation, atomically
latch `locked` with reason `external-parameter-change`. This includes detected
changes made with the owner's physical remote or knob, whether they reduce or
increase output. The lock belongs to the device controller, not to the current
guest session.

Locking invalidates the control epoch/grants, advances the execution generation,
clears pending desired state and invokes DC-05's serialized Stop path. No
subsequent motion/configuration writes from cancelled work may execute. An
already-issued I/O operation cannot be recalled; only the verified stopping
operation is permitted afterward. Do not restore the previous speed, pattern,
travel envelope or sensation to "correct" the physical override. Stop must still
explicitly command zero when feedback already reports zero.

Guest web receives a locked status with an admin-unlock-required reason and
rejects further control input. The status is not physical-stop confirmation.
Admin independently rejects it even if guest web forwards it. Stop, leave and
read-only status remain available. The guest, its session capability and
ordinary session/arming APIs cannot unlock the controller. Both management UI
and admin CLI use one explicit private unlock operation.

#### Attribute readback without masking external control

Track the last established parameter baseline and the exact expected changes of
the single active transition, scoped by connection, controller generation,
transition step and bridge write operation. Process every fresh parameter
observation through the serialized controller owner. The bridge may classify an
observation as `verified-owned-write` only when the attribution capability
correlates it to that exact issued operation and it matches the profile's units,
rounding and intermediate-state rule. A matching notification value or arrival
while a write is pending is not correlation evidence.

Compare control parameters and operating mode, not ordinary changing telemetry
such as position or time. Requested BLE speed and knob-scaled effective speed
are distinct representations: compare each only through the profile's qualified
mapping and mapping revision, never as equal scalar values. A changed effective
speed with no correlated controller write, or a verified physical-control
change, is unexplained and locks even when it matches a previous or future
desired tuple.

An `ambiguous` parameter observation, unexpected representation or
unattributable fresh change is fail-closed: latch the DC-09 lockout and request
Stop. A `not-fresh` observation cannot alter the baseline, clear a lock or
authorize another motion write; missing required fresh feedback follows DC-07's
fault path. This may conservatively lock a profile that cannot correlate its own
echoes. Such a profile is not eligible for physical remote control until
qualification supplies reliable causal attribution.

#### Explicit unlock, with no motion resumption

The operator must invoke **Unlock** on the local admin surface with the current
lock revision exposed by the immutable controller snapshot. The serialized
controller owner treats Unlock as a compare-and-set: it first verifies that
revision, retains the lock, invalidates the current generation and waits for
every issued bridge operation, including logically timed-out work, to settle.
If an operation cannot settle, Unlock fails with the existing unknown/fault
outcome and no new connection or writer is created.

After settlement, the controller requests the serialized Stop path and requires
a new, fresh, qualified zero-output observation with a sample identifier later
than the observation that triggered the lock. It validates the current policy
envelope and profile attribution capability, then rechecks the same lock
revision immediately before committing the zero baseline and clearing the
latch. A fresh external, ambiguous or unattributable parameter change during
recovery advances the revision, so the compare-and-set fails and the controller
remains locked. Do not silently rebase tracking onto an external change to make
Unlock succeed.

Successful unlock establishes a new baseline at zero and transitions to paused,
not ready/armed. Separate explicit admin arming, a fresh epoch/grant and a new
guest Send action are required before motion. Repeated Unlock after success is
an idempotent no-op; it cannot issue motion or fresh grants.

The lock does not clear on parameter return, new invitations, session expiry,
policy edits, circuit/hub reconnect or device reconnect. Fault handling and
successful Stop preserve it. Admin restart discards sessions and starts
disarmed, requiring explicit admin recovery before any new arming; restart must
not restore old authority. No persistent movement history or account system is
needed for this fail-closed startup.

#### Physical override qualification

This prevents the service from undoing an _observed_ physical override. It does
not make the physical remote an independently proven emergency stop. The
selected stock profile currently has feedback limitations: measure whether
remote/knob changes are observable, how long detection and stopping take, and
whether feedback can distinguish expected readback. Changes producing identical
observable values cannot be attributed reliably. If the required override cannot
be detected on a combination, that combination cannot be qualified for physical
remote control.

## Code style

Use nullable-aware C# with explicit ownership and asynchronous disposal. Use
explicit result/state variants instead of success booleans with optional error
fields. Keep native/bridge exceptions and device types inside the adapter;
consumer errors use stable categories and explicit unknown outcomes. Do not add
casts that bypass contract validation.

Reuse shared test fakes and capability fixtures. Keep expected cancellation
distinct from faults, propagate cancellation through owned operations, and do
not confuse request cancellation with cancellation of native device I/O. Avoid
global mutable registries and test-only production behavior.

## Testing strategy and success criteria

Use test-first changes and the lowest boundary that captures behavior. Before
fixing each retained defect, run its regression and record an intended failure;
a source inspection is not that proof. If a baseline change removes a finding,
retain evidence of non-applicability instead of inventing a failing test.

| Criterion                    | Lowest useful boundary and acceptance evidence                                                                                                                                                                                                                                                                                             |
| ---------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| Initialization               | Controller tests reject every failing stage; ready is never emitted early; no subsequent nonzero writes, including late completions                                                                                                                                                                                                        |
| Physical knob                | Adapter tests require configuration `true` and matching readback; hardware evidence covers zero/intermediate/maximum knob settings                                                                                                                                                                                                         |
| Single writer                | Controllable fake GATT tracks in-flight operations; maximum is one under rapid mixed local inputs and Stop                                                                                                                                                                                                                                 |
| Coalescing                   | Block one operation, submit many changes, and observe only one replaceable pending desired tuple; superseded requests settle explicitly                                                                                                                                                                                                    |
| Cancellation                 | Inject Stop, fault, disposal, device loss and guard revocation before/after every asynchronous boundary; no later motion write from the old generation. Revoke the applied guard with no pending input and observe the Stop path.                                                                                                          |
| Timeout                      | Hold a transport promise past its deadline; controller reports unknown state, does not start a concurrent write and cannot resume on late completion                                                                                                                                                                                       |
| Validation                   | Boundary and generated tuples prove invalid values, unsupported patterns and unsafe intermediate geometry never write                                                                                                                                                                                                                      |
| Live limits                  | Compatible changes preserve Ready/epoch/sequence and original lease deadlines; stale policy/grants reject; conflicting accepted, observed, pending, in-flight and unconfirmed writes reject the whole policy atomically; edits never bypass Stop, faults or external locks                                                                 |
| Feedback                     | Malformed, stale, cached, missing and mismatched-connection feedback cannot confirm readiness or stopped state                                                                                                                                                                                                                             |
| External-control lockout     | Inject an unexplained change to each supported parameter or operating mode, including zero-speed/knob changes, at rest and at every asynchronous transition; observe locked status, invalidated grants, cleared pending state and no stale motion write                                                                                    |
| Readback attribution         | Only bridge-correlated own write echoes with matching connection, generation, step and operation identifiers update the baseline; qualified intermediate values and normal position/time telemetry do not falsely lock; unrelated changes, even to a value in a previous/future desired tuple, are not hidden by pending work or broad numeric tolerances |
| Ambiguous attribution        | Cached/command-response data never renews the baseline; an ambiguous, unexpected-representation or unattributable fresh parameter change latches DC-09 and requests Stop, including when its value equals an expected write                                                                                                               |
| Physical override precedence | After observed physical zero, moving the physical control back or delivering a late guest request never restores motion; the explicit Stop write is not skipped because feedback is zero                                                                                                                                                   |
| Admin unlock                 | Guest hub operations, regular resume, new link, reconnect and fault recovery cannot clear lockout; successful explicit AdminWeb operator unlock remains paused; stale lock revision, unsettled I/O, stale/nonzero feedback or concurrent external/ambiguous change keeps it locked                                                              |
| Cleanup                      | Repeated connect/dispose and partial setup leave no owned listeners, timers or notifications; the discarded application's range widget is not imported                                                                                                                                                                                     |
| Guest UI wiring              | One Chrome browser talks only to the real ASP.NET Core service using a fake bridge; no Bluetooth API, second browser or device credential is required                                                                                                                                                                                      |
| One-box operation            | Browser, AdminWeb, GuestWeb, ControlService and fake bridge work on one computer with external networking blocked, unauthenticated loopback AdminWeb access and unchanged guest capability, policy and expiry rules                                                                                                                     |
| Invitation isolation         | Automatic reusable-link retrieval and retries produce no device writes, motion authority or changes to an active controller; Cycle and operator Stop and end session atomically rotate the link, revoke guest authority, clear input and request serialized Stop without automatic rearming; guest leave/disconnect/expiry retain the link |
| Accountless access           | Local operator access requires no token and has no access expiry; guest invitations require no user records or login; wrong-role, expired and revoked guest capabilities are rejected                                                                                                                                                      |
| Process separation           | GuestWeb has no operator methods, bridge references or device permissions; its hub contract cannot call arbitrary device operations. The unauthenticated loopback AdminWeb listener does not isolate compromised local processes.                                                                                                          |
| Hub validation               | ControlService rejects malformed, wrong-role, expired, cross-session, expired/superseded-grant and replayed hub messages even when GuestWeb claims acceptance; duplicate/out-of-order input sequences and the eleventh run input in a rolling second reject; transport loss/restart enters Stop/teardown                                      |
| Two-process one-box          | Both services run on one computer without cloud dependencies; generated links target guest web, not admin; guest failure never starts a fallback writer                                                                                                                                                                                    |
| Server enforcement           | Bypass the rendered controls with malformed, expired, wrong-role and over-limit requests; no forbidden operation reaches the bridge through UI, API or socket entry points                                                                                                                                                                 |
| Guest loss                   | Close/freeze the browser while the service runs; server grant expiry latches Stop without a guest callback. Retained state/server timers cannot renew authority; reconnecting a circuit does not restore motion or replay input                                                                                                            |
| Hosted lifetime              | Multiple requests/circuits share one device owner; scoped capability context is not captured globally; request cancellation cannot skip Stop or create a second writer                                                                                                                                                                     |
| Replaceability               | Portable C# controller/protocol tests run without ASP.NET Core or BLE; native bridge tests verify mapping, error outcomes and physical-I/O lifetime                                                                                                                                                                                        |
| Service races                | Concurrent endpoint/timer/bridge callbacks cannot issue a write after an earlier Stop latch or mutate a replacement connection                                                                                                                                                                                                             |
| Speed semantics              | With knob zero or a request/effective-speed mismatch, Stop still writes zero and a changed BLE speed request is not skipped                                                                                                                                                                                                                |
| Profile semantics            | Parse the selected firmware's full known state shape; stored reads/command responses do not masquerade as fresh telemetry or stopped evidence; interior values do not inherit an unverified minus-one correction                                                                                                                           |
| Hardware qualification       | Separate guest loss (live server stops) from ControlService kill/freeze, host OS sleep, bridge/BLE loss, calibration/range transitions and competing device clients                                                                                                                                                                     |

Use a controllable clock and deferred operations, not real sleeps, for ordering
and expiry tests. Test actual operation logs and observable outcomes, not
private implementation shapes. Use xUnit v3/MTP v2, FastMoq where appropriate
and FakeTimeProvider for controller tests, real service-boundary tests for
authorization, and Chrome for the guest UI. Native bridge/hardware evidence is
separate; no upstream JavaScript harness is selected.

**Unresolved rate-limit discrepancy:** the existing ControlCore requirement
accepts at most 10 run-input attempts in a rolling one-second window, while the
existing implementation/design also describes a 20-capacity bucket. These are
not equivalent limits. This specification records the mismatch without changing
either value; its resolution needs an explicit requirement decision.

### Software timing profile

Select these bounded engineering defaults for the simulator and initial adapter
work. They are project decisions, not measured hardware limits or timings
borrowed as safety guarantees from another product.

| Boundary                                                    | Default           | On expiry                                                                          |
| ----------------------------------------------------------- | ----------------- | ---------------------------------------------------------------------------------- |
| Bridge connection and discovery after operator provisioning | 5,000 ms overall  | Logical fault; retain ownership of unsettled native work during cleanup            |
| Initial stopped/configured/readiness stage                  | 5,000 ms overall  | Fault; never publish ready                                                         |
| Explicitly consented local navigation                       | 30,000 ms overall | Fault; no subsequent nonzero write                                                 |
| Each bridge GATT read/write/notification-setup operation    | 1,000 ms          | Fault; retain ownership until the operation actually settles                       |
| Fresh feedback after a required transition or Stop write    | 1,500 ms          | Mark unconfirmed and fault; no speed restoration                                   |
| Live-feedback age, where the profile supports it            | 1,500 ms          | Invalidate motion and request Stop                                                 |
| Serialized diagnostic/readback sampling opportunity         | 500 ms            | Coalesce; never queue reads ahead of Stop or claim a stored read is a fresh sample |
| Controller expiry-check timer                               | 50 ms             | On a late callback, process expiry before other pending work                       |

The 5-second readiness and 30-second navigation defaults retain the reference
application's bounded waits. The other values are explicit initial engineering
budgets, to verify on the selected bridge platform. Operator provisioning does
not start motion and is outside connection timing. An overall stage deadline is
not renewed by each successful sub-operation; the earlier deadline wins.

Test immediately before, at and after each deadline, with expiry at
`now >= deadline`. The server-issued guest grant remains 750 ms from issuance;
no longer device deadline extends it. Test with paused timers, delayed bridge
completion and callbacks delivered out of order. Service/OS scheduling, BLE loss
detection and mechanical stopping remain outside a real-time guarantee.

Do not lengthen these defaults at runtime to hide failures. A profile change
requires contract tests and qualification review.

Software acceptance requires all applicable regression and contract cases, the
focused build, and UI smoke to pass. Physical-use acceptance additionally
requires an approved stop-time bound and independent device-side behavior under
service/OS/bridge failure. An average latency, timer callback or command
acknowledgement cannot satisfy that gate.

## Boundaries

The approved [map](../README.md) governs overall scope and ownership.

- Always: keep one writer; preserve knob limiting; invalidate stale work;
  distinguish unknown physical state; retain source/licence provenance; keep
  motion disabled for unqualified profiles.
- Ask first: source imports and dependency installation during implementation,
  changes to the selected licensing/reuse decision, schema/contract changes,
  additional firmware/platform support, or physical release criteria.
- Never: expose raw BLE to guest-facing consumers, resume motion on reconnect,
  treat a timeout as cancelled hardware I/O, replace stock firmware implicitly,
  or claim physical qualification from software tests.

This specification authorizes no branch changes, code imports, dependency
installation, commits, device operation or firmware updates.

## Remaining evidence and approval gates

The current direction is ControlService with separately hostable .NET 10
AdminWeb and GuestWeb clients, with one ControlService-owned controller and
bridge. Review this revision before Plan.
Candidate firmware/pattern, mapping ownership and deadline policies remain
selected; SDK/NuGet installation approval is still required before execution.

The following are evidence or acceptance gates, not questions to answer by
copying another project's constants:

| Gate                                      | Owner and required evidence                                                                                                                                                                    | Until satisfied                                                                                             |
| ----------------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ----------------------------------------------------------------------------------------------------------- |
| Actual installation and travel            | Hardware operator supplies the exact installed stock build, hardware/calibration record and an unloaded setup; maintainer records the qualified profile                                        | Simulator/read-only identification only; no qualified physical motion profile                               |
| Feedback and parameter conformance        | Maintainer and operator verify raw-versus-effective speed, direct parameter round trips, physical envelope mapping, genuinely fresh state, stopped feedback and connection-scoped causal attribution for every supported parameter/mode write | Required capabilities remain unavailable; physical remote arming disabled |
| Physical override detection               | Verify physical remote/knob changes trigger DC-09 lockout without the service restoring prior motion; distinguish qualified own echoes from external changes, and record detection latency and actual stop behavior on each supported profile | No claim that the physical remote is an effective hard stop through this feature until demonstrated         |
| Independent stopping and acceptable bound | Maintainer and hardware operator agree an application-specific maximum stopping time/distance before loaded use, then collect unloaded fault evidence from fault onset through actual stopping | No physical-use release; the source's one-second delay plus two-second ramp is not accepted as a safe bound |
| Bridge deployment                         | Establish actual home-service OS, native BLE/bridge hardware, service identity, permissions and pairing/provisioning procedure                                                                 | Simulator bridge only; no invented platform-specific adapter or untested container Bluetooth                |
| Operator actions                          | Define trusted service-side approval, device setup and rearming without a second browser; ordinary guest requests cannot perform them                                                          | Sessions remain disarmed until explicitly allowed                                                           |
| Service integration                       | Verify authorization across all entry points, single hosted-controller lifetime, native I/O cancellation and guest-loss expiry                                                                 | No claim that rendered UI restrictions or a C# fake prove the enforcement boundary                          |
| Build and dependencies                    | Implementation work pins a serviced .NET 10 SDK, compatible NuGet/workload versions and a reviewed restore after installation approval                                                         | No claim of build reproducibility, dependency safety or passing regression tests                            |

The independent-stop test includes ControlService process death/freeze with BLE still
connected, host OS sleep, bridge loss and competing-client conditions. Guest web
or Chrome death/freeze is separately handled by live ControlService. A
healthy BLE connection is not proof admin is executing. If unchanged firmware
cannot meet that boundary, return for an explicit scope decision about an
independent interlock or a different device adapter; do not substitute a manual
power cut for an automatic stop.

No implementation tasks, later module specifications, firmware updates or
physical operation are approved by resolving these design questions.

## Sources

[app]:
  https://github.com/ReadieFur/OSSM-Web-Control/tree/e65032ba2ed9c5709d91e5afd4f7034d7965fa32
[bundle]:
  https://github.com/ReadieFur/OSSM-Web-Control/blob/e65032ba2ed9c5709d91e5afd4f7034d7965fa32/src/ossm-ble/ossmBle.js
[declarations]:
  https://github.com/ReadieFur/OSSM-Web-Control/blob/e65032ba2ed9c5709d91e5afd4f7034d7965fa32/src/ossm-ble/ossmBle.d.ts
[ble-source]:
  https://github.com/ReadieFur/OSSM-BLE-Web/tree/d2a9de6cb8fbdeaba5a2d4345c69d7a70d4789f2
[firmware]:
  https://github.com/KinkyMakers/OSSM-hardware/tree/4871a84c54ae0734b1f8bc3d02fcc0098f12fbad
[knob-call]:
  https://github.com/ReadieFur/OSSM-Web-Control/blob/e65032ba2ed9c5709d91e5afd4f7034d7965fa32/src/script.ts#L1191-L1202
[play-input]:
  https://github.com/ReadieFur/OSSM-Web-Control/blob/e65032ba2ed9c5709d91e5afd4f7034d7965fa32/src/script.ts#L1093-L1189
[navigation]:
  https://github.com/ReadieFur/OSSM-Web-Control/blob/e65032ba2ed9c5709d91e5afd4f7034d7965fa32/src/script.ts#L797-L835
[initial-state]:
  https://github.com/ReadieFur/OSSM-Web-Control/blob/e65032ba2ed9c5709d91e5afd4f7034d7965fa32/src/script.ts#L782-L795
[cleanup]:
  https://github.com/ReadieFur/OSSM-Web-Control/blob/e65032ba2ed9c5709d91e5afd4f7034d7965fa32/src/script.ts#L612-L622
[slider-disposal]:
  https://github.com/ReadieFur/OSSM-Web-Control/blob/e65032ba2ed9c5709d91e5afd4f7034d7965fa32/src/styles.ts#L103-L145
[firmware-disconnect]:
  https://github.com/KinkyMakers/OSSM-hardware/blob/4871a84c54ae0734b1f8bc3d02fcc0098f12fbad/Software/src/services/communication/nimble.cpp
[firmware-protocol]:
  https://github.com/KinkyMakers/OSSM-hardware/blob/4871a84c54ae0734b1f8bc3d02fcc0098f12fbad/Documentation/ossm/Software/communication/ble.mdx
[firmware-version]:
  https://github.com/KinkyMakers/OSSM-hardware/blob/4871a84c54ae0734b1f8bc3d02fcc0098f12fbad/Software/src/constants/Version.h
[firmware-state]:
  https://github.com/KinkyMakers/OSSM-hardware/blob/4871a84c54ae0734b1f8bc3d02fcc0098f12fbad/Software/src/ossm/OSSM.cpp
[firmware-play]:
  https://github.com/KinkyMakers/OSSM-hardware/blob/4871a84c54ae0734b1f8bc3d02fcc0098f12fbad/Software/src/ossm/play_controls/play_controls.cpp
[firmware-mode]:
  https://github.com/KinkyMakers/OSSM-hardware/blob/4871a84c54ae0734b1f8bc3d02fcc0098f12fbad/Software/src/ossm/stroke_engine/stroke_engine.cpp
[firmware-engine]:
  https://github.com/KinkyMakers/OSSM-hardware/blob/4871a84c54ae0734b1f8bc3d02fcc0098f12fbad/Software/lib/StrokeEngine/src/StrokeEngine.cpp
[firmware-patterns]:
  https://github.com/KinkyMakers/OSSM-hardware/blob/4871a84c54ae0734b1f8bc3d02fcc0098f12fbad/Software/lib/StrokeEngine/src/pattern.h
[xtoys-admission]:
  https://github.com/denialtek/XToys-Guide/blob/bc3d9f1e5ea06619ccfe6403d33f147bb1f3022e/docs/getting-started/online-play.md#L15-L65
[xtoys-limits]:
  https://github.com/denialtek/XToys-Guide/blob/bc3d9f1e5ea06619ccfe6403d33f147bb1f3022e/docs/getting-started/using-toys.md#L45-L68
[sexcode-protocol]:
  https://gitlab.com/sexync/sexcode/-/blob/c49f08c7f76319496b76194c2a96d27dc3af4fd6/SPEC.md
[sexcode-schema]:
  https://gitlab.com/sexync/sexcode/-/blob/c49f08c7f76319496b76194c2a96d27dc3af4fd6/sexcode.fbs#L55-70
[sexync-about]: https://sexync.com/about
[possum]:
  https://github.com/RubberyFun/OSSM-Possum/tree/ebd01fcaab8c99b926ce7dd58a2cec2a879a29f7
[blazor-platforms]:
  https://learn.microsoft.com/aspnet/core/blazor/supported-platforms?view=aspnetcore-10.0
[blazor-rendering]:
  https://learn.microsoft.com/aspnet/core/blazor/components/render-modes?view=aspnetcore-10.0
[hosted-service]:
  https://learn.microsoft.com/aspnet/core/fundamentals/host/hosted-services?view=aspnetcore-10.0
[dotnet-test]:
  https://learn.microsoft.com/dotnet/core/testing/unit-testing-with-dotnet-test
[time-provider]:
  https://learn.microsoft.com/dotnet/api/system.timeprovider?view=net-10.0
[fake-time]:
  https://learn.microsoft.com/dotnet/core/extensions/timeprovider-testing
