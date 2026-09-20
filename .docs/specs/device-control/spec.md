# Device contracts and core execution

Module: `device-control`. Requirement IDs DC-01 through DC-09 are retained.

## Scope and ownership

This specification owns the reusable device contract, stock and simulator
adapters, and controller execution rules. The
[core composition contract](../control-core.md) defines logical components and
requirement terms. Project names are not part of this contract; interface/model
names below describe the API, not its packaging.

The device library must own transport, protocol mapping and state publication.
The controller must own lifecycle, limits, trusted execution guards and the
single writer. A consuming application must supply trusted authorization and own
their lifetimes. These components must not require guest sessions, web hosts, UI
routes or a particular orchestration application.

The [adapter design](adapters.md) records the accepted Shiny/native-simulator
direction, DI composition, proposed device-selection configuration and remaining
integration gates. The device contract and DC requirements remain owned here.

The [application contract](../application.md) owns remote invitations, approval,
grants, service transport and display. Its security checks must feed the core
execution guard; the core must not infer authority from presentation state. The
device contract must be usable directly without the controller, while an
application composing a controller must not allow callers to bypass its writer.

## Device profile, identity and feedback

The stock reference is firmware `1.0.34` at
`4871a84c54ae0734b1f8bc3d02fcc0098f12fbad`. This identifies the protocol target,
not qualification of every unit reporting that version. The simulator uses the
native OSSM-RS engine and is not a stock-firmware emulator.

The following are pinned firmware facts, not additional requirement copies:

| Evidence                                             | Reported stock behavior                                                                                                                                                                                                                                                                                                 |
| ---------------------------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| [Version header][firmware-version]                   | Identifies the selected stock version.                                                                                                                                                                                                                                                                                  |
| [Pairing characteristic][firmware-pairing]           | A live read returns `MAC;chip;wifiConnected;md5;version`; identity is not attestation or fresh motion telemetry.                                                                                                                                                                                                        |
| [Patterns characteristic][firmware-pattern-metadata] | Provides pattern names and indices.                                                                                                                                                                                                                                                                                     |
| [State serialization][firmware-state]                | JSON contains timestamp, state, speed, stroke, sensation, depth, buffer, pattern, position and sessionId. Timestamp is unsigned `millis()` uptime, which wraps/restarts. The initial session is empty; play entry generates a UUID. Position is a stepper counter in millimetres, not independent physical measurement. |
| [Play controls][firmware-play]                       | Knob percentage multiplies requested BLE speed and divides by 100; telemetry truncates effective speed to an integer. Knob position itself is not reported. Configuration reveals whether limiting is enabled.                                                                                                          |
| [BLE handling][firmware-ble]                         | State is cached READ/NOTIFY data, with no live on-read handler. Notifications follow fingerprint changes; timestamp, position and buffer are excluded. A cached `ok:<command>` after FIFO submission is not notified and does not establish applied motion. There is no effective periodic state heartbeat.             |
| [Disconnect handling][firmware-ble]                  | Menu/ramp-down paths do not establish a physical stop bound, BLE loss-detection time or behavior while another client remains connected.                                                                                                                                                                                |

### Initial parameter mapping

The initial application profile must use Simple Stroke (pattern `0`), fixed
sensation `50`, no inversion and integer percentages in `[0,100]`. Nonzero speed
must require positive stroke. Other reported patterns may be identified but must
not enter the physical qualified allowlist without evidence.

[Simple Stroke][firmware-patterns] targets depth and depth minus stroke, while
[mode mapping][firmware-mode] scales against measured travel and the
[engine][firmware-engine] clips against keepout margins. Percentages alone
therefore cannot establish a physical envelope. The adapter must own a qualified
conversion and rounding rule; DC-03 governs validation of its results.

The stock implementation must use exact field values subject to qualification,
not inherit the older BLE library's unconditional interior-value decrement.
Qualification must verify boundary/interior round trips. Any correction must
belong to a separately qualified mapping, not a global undocumented offset.

## Device abstraction and ownership

The device abstractions must own the following contract. Device implementations
must keep native handles, raw payloads and transport records private. The
abstractions must not enumerate or otherwise identify concrete implementations;
implementations describe their semantics through profile identifiers and
reported capabilities.

| Member                                                                                    | Required behavior                                                                                                                                                       |
| ----------------------------------------------------------------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `DeviceConnectionState ConnectionState { get; }`                                          | Expose current connection status independently of reported motion state. The shared base must own this getter-only property.                                            |
| `DeviceInformation Device { get; }`                                                       | Expose discovered device information for the instance lifetime; throw `InvalidOperationException` before discovery. Reading this getter must have no side effects.      |
| `event Action<DeviceConnectionState>? ConnectionStateChanged`                             | Notify connection transitions after updating the property, without publishing unrelated device states. Subscriber failures must be isolated.                            |
| `event Action<OneOf<DeviceState, Exception>>? DeviceStateChanged`                         | Publish transient immutable validated states or semantic asynchronous device errors. Subscriber failures must be isolated and must not be republished as device errors. |
| `Task SetMotionAsync(MotionSettings motionSettings, CancellationToken cancellationToken)` | Write requested settings. Completion must mean the operation settled, not that feedback arrived or a physical outcome was measured.                                     |
| Inherited `ValueTask DisposeAsync()`                                                      | Release owned I/O and subscriptions once, without resuming motion.                                                                                                      |

Device information must be discovered by the implementation, not supplied by
callers or constructor arguments, and must never be fabricated. Once discovered,
it must remain available for the instance lifetime, including after
disconnection. Reading `Device` must not start discovery or authorize motion.

`IOssmDevice` must not add a separate Stop, initialization/connection or
command- confirmation method, or a public command sequence. Adapter
lifetime/setup must remain implementation-owned. Application/core Stop must use
`SetMotionAsync` with zero speed, preserving other settings. It must write zero
even if reported effective speed is already zero, and must not navigate to the
menu or enter/ re-enter play. DC-05 owns scheduling and authority invalidation
for that write.

`DeviceState` must be a public sealed record representing actual reported state;
`DeviceSnapshot` must remain absent. `DeviceState` must use explicit
`get; init;` properties rather than a positional constructor: required
`DateTime Timestamp`, `MotionSettings MotionSettings` and `Guid SessionId`, plus
optional `double? VelocityMillimetresPerSecond`. It must not include submitted
settings or freshness metadata. Timestamp and reported settings are required;
velocity remains optional because some transports do not report it.

Connection status and discovered device identity/profile/capabilities must
remain device properties, not state fields. The state must not carry a
`CapturedAtUtc`, `Fault`, `Device` or `ConnectionState` property, duplicate
reported velocity, or embed errors. It must not expose guest sessions, grants,
UI state, application arming/lockout, native handles, raw payloads, command
identifiers or causal-attribution claims. Before the first state report, no
state event is published. Absence must not be represented by a partial
`DeviceState`, a sentinel timestamp or fabricated zero settings or velocity. A
real zero report keeps its sample timestamp and settings.

Connection state must contain only disconnected, connecting and connected.
Before startup and whenever no connection remains, the state must be
disconnected. A new connection attempt transitions from disconnected to
connecting. Reporting an error must not change connection state by itself;
transport termination or a failed connection attempt must report disconnected.
Connection attempts, transitions and transport failures must be diagnosed with
structured logs at the shared lifecycle owner; connection failures must not be
published through `DeviceStateChanged`. Its exception arm is reserved for
semantic asynchronous device errors. Persistent fault/recovery policy belongs to
the controller. Disposal remains object lifetime, not a connection state.

An incoming state report must have both a UTC timestamp and reported
`MotionSettings`; publication must reject incomplete states with
`InvalidDataException` before invoking subscribers or mutating pending
expectations. Velocity must be finite when supplied, must belong to that report,
and must remain unknown when the transport does not report it. Speed percentages
must not be converted into fabricated velocity; stock must leave it unknown.
State timestamps must be UTC and remain unchanged when published. Stock uptime
must remain private sample metadata, not be cast to `DateTime`. Empty stock
sessions must map to `Guid.Empty`; later values must parse as UUIDs. Device
sessions are not guest capabilities.

`MotionSettings` must retain defined state/pattern enum values, integer Speed,
Stroke, Sensation, Depth and Buffer in `[0,100]`, and finite Position in
millimetres. Settings and implementation options must use DataAnnotations for
property constraints and `IValidatableObject` for model/cross-field constraints.
`DeviceState` must own its UTC timestamp, reported-settings and finite-velocity
validation. Every device entry point must invoke standard model validation
because annotations alone cannot validate construction. State labels must map
explicitly to firmware strings, and pattern identifiers `0`–`6` must retain
SimpleStroke, TeasingPounding, RoboStroke, HalfAndHalf, Deeper, StopAndGo and
Insist respectively. Profile meaning must remain explicit.

On input, Speed is requested speed; in reported states it is effective speed.
Position is report-only, and reported mode is not navigation permission. The
caller must not use the record as a position-streaming or homing API.

### State publication semantics

Each validated incoming state must be published transiently through
`DeviceStateChanged`. The device abstraction and shared base must not cache
complete `DeviceState` instances. State events must originate from validated
incoming state, and previously delivered states must remain immutable.

The adapter must keep responsiveness separate from state feedback. Notification
silence alone must not imply disconnection, and it must not change motion to
manufacture feedback. Cached reads, write completion and successful liveness
probes must not be published as new motion state. Required-feedback tracking and
freshness deadlines belong to the controller under DC-07.

`OssmDeviceBase` in the stock library must own common state publication,
model-validation enforcement, pending-write expectation registration before
dispatch, operation settlement, semantic fault reporting, subscriber isolation
and repeated cleanup. It must not retain complete `DeviceState` instances or
provide a replacement state cache. Its public invariants must not be
overridable. Protected hooks must be limited to transport lifetime, settings
dispatch and implementation-specific state assessment. The stock implementation
must assess knob scaling and integer truncation; the simulator must use direct
engine evidence. The base must not add controller authority, arming, persistent
lockout, Stop priority or a second desired-state queue.

Concrete implementations must receive their own validated `IOptions<TOptions>`
and own connection startup and shutdown. Composition must register one concrete
instance as both hosted service and `IOssmDevice`, selected through dependency
injection at startup. Controller and ordinary application paths must not select
or branch on concrete adapters; the composition root owns that choice.
Superseded adapter references must be removed during integration, not retained
as aliases or parallel execution paths. Constructors must not perform
asynchronous I/O or authorize calibration, navigation or motion.

Awaited methods must throw standard .NET exceptions by meaning. Provider access,
timeout, transport and malformed-data failures must map to
`UnauthorizedAccessException`, `TimeoutException`, `IOException` and
`InvalidDataException`, preserving obscure provider failures as
`InnerException`. Caller cancellation must remain `OperationCanceledException`;
argument and cancellation failures must not be broadcast. Semantic asynchronous
errors must be published only through the `Exception` arm of
`DeviceStateChanged`. Connection attempts, transitions and transport failures
must use lifecycle logging instead. An unexplained reported speed change must
publish `UnexpectedSpeedChangeException` after publishing the new state, without
throwing from a native callback. BLE work deferred from this increment must
throw `NotSupportedException`; placeholders must never report a connected device
or successful write.

## Stock firmware implementation

The stock adapter must target the pinned firmware:

1. It must use Shiny in the owning .NET process, without a BLE helper process,
   and own one native BLE connection and its subscriptions. The selected package
   version, host OS, service-account permissions and hardware must be verified
   against the
   [adapter integration gates](adapters.md#compatibility-findings-and-integration-gates).
   It must remain disarmed during startup, await operator-authorized
   setup/navigation and enable/read back knob limiting. It must not add
   automatic reconnect or speculative reconnect-generation machinery.
2. It must translate writable settings to stock `set:*` commands and serialize
   each operation, respecting the controller's Stop/generation checks between
   writes. Zero speed must map to `set:speed:0`. Redundant-write detection must
   compare submitted requests, not effective reported speed. Changed requests
   must still write when equal to the current knob-scaled reading. Ordinary
   settings writes must exclude navigation and report-only fields.
3. It must distinguish state JSON, `ok:<command>` and malformed responses.
   Validated state notifications must be published under the shared semantics.
   Cached, non-notified acknowledgements must not become an asynchronous
   confirmation protocol.
4. Tracking must retain only the last reported speed, last submitted settings
   and bounded pending expectation needed by DC-09. It must not retain complete
   device states. Notification handling and expectation bookkeeping must be
   serialized. Semantic asynchronous detection must use the state/error event,
   not throw from native callbacks. Faulted adapters must reject nonzero writes
   while allowing serialized zero writes.
5. It must use BLE lifecycle events and operation failures for connection state.
   It may probe responsiveness with a bounded pairing/configuration read. It
   must read firmware identity from pairing and names/indices from patterns,
   without requiring manual firmware/profile selection when identification
   works. Physical peripheral enrollment and later locator resolution are
   distinct from profile identification; the
   [adapter design](adapters.md#physical-device-selection-and-connection)
   describes that selection. These reads must not grant physical qualification
   or advance the controller's feedback deadline.

## Simulator implementation

The simulator must implement the same device contract using the existing
`tools/ossm-sim-bridge` executable and `ossm-rs/ossm-motion` engine at
`578161b2e78d8ca70f8909f0ec34b8ae6014cfda` (Apache-2.0). It must not introduce a
second C# engine, import GUI saved motion or modify the sibling Rust checkout.
Its private profile must remain `ossm-rs-sim-v1`; its mapping and possible
retraction are not stock behavior or hardware qualification.

The adapter must map native data directly to the same `DeviceState` and
`MotionSettings` shape, units and public event semantics as the stock adapter.
The application must not require simulator-specific parsing or control paths.
Profile identity and supported capabilities must remain truthful; transparent
consumption must not fabricate stock behavior or hide unsupported settings.
Motion-only simulation without stock-firmware or BLE emulation is accepted for
the current scope. The [adapter design](adapters.md#transparent-simulator-data)
describes the mapping and limits.

Shared speed/expectation bookkeeping may live in the stock implementation with
overridable operations. The simulator must use direct engine
settings/position/velocity and may override stock transport and inference. Reuse
must not require BLE caching, knob estimation, polling or a strategy/factory
layer per method.

- Lifetime must use `BackgroundService` and validated typed options. The
  configured executable must start once, with no command-line arguments. The
  background task must remain alive with its reader, and shutdown cancellation
  must reach owned work. Hosted-service and device-interface registration must
  use the same instance, not two child owners.
- Transport must use bounded redirected UTF-8 JSON Lines over stdin/stdout and
  separately drained stderr. Outstanding requests must be bounded. Private IDs
  may correlate replies without becoming public contract fields.
- Zero-speed `SetMotionAsync` must use the existing engine zero-speed operation.
  Completion must settle the operation; the resulting reported state must be
  published separately. Engine settlement required by safe transitions must
  remain private and must not impose public Enabled/Moving fields.
- The adapter should use private sample/change metadata where useful instead of
  reconstructing engine truth through stock heuristics. Frozen feedback must not
  be replayed; no `DeviceStateChanged` state events are published while feedback
  is frozen, even if the process responds.
- Identity must use the known simulator profile. Operator/test-only injection
  must cover unexpected speed changes (P0), other settings (P2), delayed
  completion, frozen feedback and disconnect. No guest control may expose it.
- Process exit/protocol failure must settle pending requests with errors, update
  connection state and be diagnosed through lifecycle logs. It must not publish
  a `DeviceStateChanged` error. Disposal must release readers, streams and the
  child. Simulation must not open BLE or drive physical hardware.

## Controller execution requirements

### DC-01: Explicit lifecycle and readiness

The controller must distinguish disconnected, initializing, ready, paused,
locked, faulted and disposed states. Only ready may accept nonzero motion.
Lockout must remain latched alongside faults/disconnects; normal initialization
or fault recovery must not clear it. Device selection and pairing must require
trusted owner setup, not untrusted consumer input.

Initialization must submit zero, obtain stock knob-limit enable/readback or
direct simulator state, validate reported readiness, await required navigation
and satisfy DC-07's feedback rules before publishing ready. Each step must have
a finite deadline and recheck revocation. Merely reaching a play-page
initializing/preflight state must not imply readiness.

Navigation that can home/calibrate must require explicit local consent, never
implicit unknown-state recovery. Initialization failure must fault, inhibit
motion and clear pending work. Serialized zero may still be attempted when
transport permits; no late step may write nonzero or publish ready. Recovery
must require fresh explicit trusted initialization at zero. Device readiness
must be independent of application/service availability.

### DC-02: Narrow consumer surface

The controller must expose only initialization, complete desired-state
submission, Stop, trusted resume/recovery/unlock, immutable state feedback and
disposal. It must not expose raw commands, arbitrary method invocation, position
streaming or firmware operations. These are controller responsibilities, not
additional `IOssmDevice` methods.

Every submitted tuple must have a trusted in-process execution guard and a
request revision. Accepted requests must receive one terminal applied,
superseded, rejected, cancelled or faulted outcome. Acceptance is not transition
completion, and completion is not independent mechanical measurement.

The mandatory guard must decide synchronously under current authorization,
policy and controller feedback-freshness rules. It must not originate from an
untrusted payload or perform asynchronous work. Guard errors must fault the
controller, not default to permission. The controller must also enforce its
generation/device invariants.

The core must retain the guard for applied nonzero output and evaluate it on the
expiry timer even without pending work. Revocation must latch Stop without
waiting for another input while the owner can execute. Completion must not
extend or detach authority. Application-specific grants and credentials must
remain outside the core contract.

Every authority-restoring action must supply the expected authority revision.
Stop must advance that revision and reject in-flight restores carrying the old
value. Resume/recovery must preserve zero until a new valid request; faults must
require full initialization and locks must use DC-09.

### DC-03: Capability and value contract

Device information must identify its profile and reported capabilities without
an abstraction-owned implementation taxonomy. Profiles must distinguish field
units, resolution and reported states from physical qualification. Requested
settings must remain distinct from effective reported states. Profiles must give
identifiers explicit meaning; consumers must not infer equivalence from numbers
or names.

Requests must supply a complete pattern/speed/stroke/depth/sensation tuple.
Validation must reject wrong types, nonfinite values, unsupported patterns,
invalid range/resolution and combined geometry without coercion, silent clamping
or fallback patterns. Both the desired envelope and every intermediate tuple
must fit the profile mapping and local permitted envelope without relying on
firmware clipping. The adapter owns mappings/update order; the controller owns
enforcement of the caller's permitted envelope. Execution must enforce both.

Limits must be applied atomically. A proposal conflicting with accepted, active
reported feedback, pending, in-flight or completed-but-unconfirmed writes must
reject as `limits-conflict` before mutation, including zero-speed envelope
writes. Rejection must retain prior policy, authority, desired state and writer.
Compatible changes must preserve ready state, generation and accepted
guard/deadline; they must not pause, clamp, rearm or permit a forbidden
transient. Identical limits must be a no-op. Policy edits must not clear
faults/locks or revive expired authority. Application policy versions and grant
publication are outside this core rule.

### DC-04: One writer and bounded pending work

One execution owner must serialize whole transitions and all competing device
operations, including initialization, configuration, maintenance, fault
handling, Stop and request/response reads. The device implementation must not
add an independent queue that bypasses this owner's cancellation/generation
checks.

State mutation, Stop latching and final validation/write issuance must have one
serialized owner. Final checks and issuance must be ordered against Stop;
acceptance into a queue is not device execution. No lock may be held across
awaited device I/O.

There must be at most one in-flight transition and one replaceable pending
desired-state slot. The slot must retain its tuple, revision and guard together,
never combine one request's values with another's authority. Checks for lockout,
generation, guard and tuple validity must run on receipt, after each await and
immediately before each write. Opaque multi-write helpers may be used only if
they enforce the same checks at every write.

### DC-05: Stop wins without concurrent writes

Stop must synchronously inhibit motion, advance generation/authority revision
and clear pending motion. It must enter paused unless an existing lock/fault
must stay latched, and invalidate pending adapter work before another write.
Repeated Stop must be idempotent without a backlog. It must not require a valid
motion grant; an application's caller authentication remains its responsibility.

Already-issued I/O cannot reliably be recalled. After it settles within its
bound, the controller must issue the shared contract's zero-speed write before
ordinary work. Cancelled transitions must not issue more motion or publish
success. A queue reset must not create a second writer.

On timeout, the controller must fault and expose unknown physical state while
retaining ownership of the underlying operation. Cancellation or a rejected
timeout wrapper must not be treated as settlement. It must not issue concurrent
Stop writes or replace/reuse the connection prematurely. If I/O later settles
and transport is usable, serialized zero may be attempted, but fault must
remain.

Outcomes must distinguish Stop latched, zero write settled, reported zero and
unconfirmed. Successful writing must not update reported speed. Operational
zero-speed evidence does not establish independent physical stopping; the
application owns its user-facing status and physical-stop instruction.

### DC-06: Safe transitions and local maintenance

Before changing pattern/envelope, the controller must command and confirm zero
under DC-07, apply the verified intermediate sequence while stopped and validate
the result. Requested speed may resume only with the current generation and
valid guard. Failure must remain stopped/faulted, never restore prior speed.

For Simple Stroke the nominal sequence is zero, shrink stroke to the smaller of
current/desired values, change depth, then expand stroke. Each step must satisfy
DC-03. Transitions requiring a different sequence must reject rather than guess
a repair. Simulator tests must prove intermediate containment; hardware
qualification must verify the physical mapping separately.

Initial homing/calibration must use the device's physical controls locally.
There must be no remote/UI calibration added to obtain readiness. A future
maintenance surface needs its own approved contract. Switching devices must
invalidate and dispose the old connection before a new controller accepts
motion.

### DC-07: Feedback, loss and disposal

Notifications/readbacks/metadata must be treated as untrusted and validated
before publication. The controller must consume transient device states, own the
latest validated state and its monotonic freshness deadline, and track submitted
intent separately. Write completion must settle I/O bookkeeping, not an
outstanding feedback expectation.

A matching state report received within the controller's feedback deadline must
satisfy the expected result. For initialization, Stop and transitions, a still-
valid already-satisfied controller baseline may supply evidence if no
intervening write or state report contradicts it. The controller must not demand
duplicate change-only notifications or refresh the baseline's deadline. Explicit
Stop must still write zero. Cached protocol values and write completion alone
must not satisfy new feedback requirements. Matching establishes consistency,
not exclusive causation; no public firmware command ID, sample sequence or
causal- attribution framework is required.

Missing/invalid required feedback at its deadline must remain unconfirmed, fault
the controller, inhibit motion and request serialized zero where possible.
Missing feedback alone must not create an external-change lock. An idle reading
exceeding the controller's freshness deadline must not imply transport failure.
DC-09 owns unexpected-change lockout; neither fault path may replay previous
settings as a repair. Active motion must still obey freshness/authority
deadlines.

The controller must check monotonic deadlines at intake, completion and expiry
timer callbacks. On resume from process/OS suspension or an overdue expiry
check, it must invalidate authority before buffered work, regardless of callback
order. Disconnect, fault and disposal must cancel pending work. Reconnection,
restart and late completion must not restore authority or replay intent.

Graceful shutdown must reject motion, invalidate authority, request bounded Stop
and dispose the device. The core must remain reachable to internal Stop after
the initiating request ends. Process failure may skip graceful shutdown and
cannot waive independent physical-stop requirements.

Cleanup must use stored callback identities, unsubscribe, cancel timers, stop
notifications and release owned resources after successful use, failure or
partial initialization. Registry removal must have one owner. Repeated cleanup
must be harmless; disposed instances must not publish. Cleanup must preserve
visible unconfirmed-stop status, not replace it with apparent successful
disconnection. Consumer/view disposal must not dispose shared core I/O.

### DC-08: Replaceability and compatibility

Adapters must satisfy one observable contract/conformance suite without changing
controller policy or consumer behavior. Direct simulator overrides may bypass
stock inference while preserving those semantics. Tests may use deterministic
fakes. Replacement must require only adapter/composition changes when semantics
match, not simulation branches or a generic plug-in framework.

Internal contracts must use explicit variants and units, not method-level
version numbers. Serialized profiles must identify their schema and preserve
published meaning. Changed semantics must require an explicit contract/profile
change and fresh physical qualification where applicable. Application wire
negotiation belongs to the application, not the device library.

### DC-09: External parameter changes latch an admin-only lockout

Here, admin means the trusted owner, not a particular web host. P0 detection
must atomically lock with `external-parameter-change` when timely reported
effective speed changes without an explaining pending speed write, including
detected knob/remote decreases to zero. Other settings/mode detection is P2 and
must use this same lock when implemented, but must not block P0 delivery.
Ordinary position/time changes must not count as overrides.

The controller must own the lock independently of application sessions. Locking
must revoke current authority, advance execution generation, clear pending
intent and invoke DC-05. After issued I/O settles, only the stopping operation
may follow; the controller must not restore previous parameters to correct the
override. Status must expose the lock and trusted-unlock-required reason, not
claim physical stopping. Stop, read-only status and application leave must
remain usable.

#### Track reported speed and pending writes

Adapter tracking must retain only the minimal prior reported speed baseline,
last submitted speed and expected change for the single pending speed write. It
must register expectations before I/O because feedback can arrive first, and
clear them after matching state feedback or the bounded feedback deadline. Old
requests and depth-only writes must not excuse later speed changes.

Without a pending speed change, changed timely reported speed must be
unexpected. Pending matching must account for stock knob scaling and integer
truncation, not demand requested/effective equality or use a broad tolerance. A
previous nonzero request may establish a scaling estimate with the previous
integer reading's rounding interval. A zero baseline cannot reveal knob
position: the first nonzero request must use the first consistent timely report
as its effective baseline, without claiming knowledge of knob position or stored
BLE request.

That bounded first match must not become permanent tolerance;
established-scaling matching must not accept every value below requested speed.
Inconsistency must lock; missing required feedback must use DC-07. The simulator
may directly identify its own writes/injected changes. Cached protocol
readings/acknowledgements must not advance adapter baselines, controller
freshness deadlines or clear locks. The adapter publishes a semantic unexpected-
speed error; the controller owns the resulting lock. Identical reported external
changes cannot be attributed exclusively by this mechanism.

#### Explicit unlock, with no motion resumption

Unlock must require explicit trusted-owner action with the current snapshot's
lock revision. The serialized owner must compare that revision, retain the lock,
invalidate the current generation and await every issued operation, including
logically timed-out I/O. Unsettled operations must make recovery fail unknown/
faulted without creating another connection or writer.

After settlement, recovery must request serialized Stop and require timely zero
evidence after lock plus a settled zero write. A zero state report that
triggered the lock and remains within the controller's feedback deadline may
supply the evidence without a duplicate notification.

The controller must validate the current envelope and recheck the same lock
revision before committing the zero baseline. New unexpected speed changes must
advance the revision and retain the lock; stale/unresolved feedback must not
clear it.

Success must leave paused, with separate arming and new valid input required for
motion. Repeated successful Unlock must be an idempotent no-op without motion or
new authority. Parameter return, policy edits, new application sessions,
successful Stop, faults or reconnect must not clear the lock. Restart must start
disarmed, discard old authority and require explicit trusted recovery; this does
not require persistent movement history or accounts.

## Software timing and conformance

These are engineering defaults, not measured physical limits. Deadlines must not
be extended at runtime to hide failure; profile changes must have contract tests
and qualification review. Stage deadlines must not renew after successful
substeps; the earlier deadline must win. Operator provisioning is outside
connection timing and must not start motion.

| Boundary                                      | Default           | Required expiry behavior                             |
| --------------------------------------------- | ----------------- | ---------------------------------------------------- |
| Connection/discovery after provisioning       | 5,000 ms overall  | Fault; retain unsettled I/O ownership during cleanup |
| Initial stopped/configured/readiness stage    | 5,000 ms overall  | Fault; no ready publication                          |
| Explicitly consented local navigation         | 30,000 ms overall | Fault; no subsequent nonzero write                   |
| Each GATT read/write/notification setup       | 1,000 ms          | Fault; retain ownership until actual settlement      |
| Required state feedback after transition/Stop | 1,500 ms          | Unconfirmed/fault; no speed restoration              |
| Controller feedback age where supported       | 1,500 ms          | Invalidate motion and request Stop                   |
| Serialized diagnostic/readback opportunity    | 500 ms            | Coalesce behind Stop; cached reads remain cached     |
| Controller expiry-check timer                 | 50 ms             | Process overdue expiry before other pending work     |

Controller conformance must cover each DC requirement using deterministic clocks
and deferred I/O, not sleeps. Deadline checks must include immediately before,
at and after `now >= deadline`, delayed completions and callback reordering.
Tests must prove one in-flight writer, bounded coalescing, invalid intermediate
tuple rejection, compatible/conflicting policy updates, and revocation before/
after every await, including applied guards with no pending request.

Adapter conformance must cover parsing, identity, scaling/truncation, zero
writes despite effective zero, no state publication for frozen simulator
feedback, cached/acknowledgement non-publication, missing-feedback faults,
external changes during own/unrelated writes, stale unlocks and partial/repeated
cleanup. Stock and simulator must be tested through their shared observable
contract without requiring application hosts. See
[core verification](../control-core.md#implementation-and-verification) for
framework/tooling and
[application conformance](../application.md#application-conformance) for
service/browser coverage. Software acceptance requires applicable contract
tests, focused builds and application smoke checks; it is not physical approval.

## Physical qualification

The physical qualified allowlist must remain empty until evidence is recorded.
Simulator support must not populate it. Scope remains one device, supervised use
and stock firmware; no replacement firmware, streaming trajectories, unattended
operation or automatic resumption is authorized.

| Gate                     | Required evidence                                                                                                                                                                                                                           |
| ------------------------ | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Installation and mapping | Exact installed build, hardware/calibration record and unloaded setup; qualified travel/rounding/keepout mapping and intermediate transitions                                                                                               |
| Protocol conformance     | Knob limiting readback and scaling at zero/intermediate/maximum, requested/effective-speed distinction, boundary/interior round trips, notification/cache behavior and zero reporting                                                       |
| Physical override        | P0 observable remote/knob speed changes, pending own writes, detection latency and actual stopping; document identical-value and zero-request blind spots. P2 settings detection must remain separate.                                      |
| Independent stopping     | Maintainer/operator-agreed maximum stopping time/distance before loaded use; unloaded fault evidence from onset through actual stop for controller process kill/freeze with BLE connected, OS sleep, adapter/BLE loss and competing clients |
| Native deployment        | Verified OS, BLE hardware, service identity, permissions and provisioning; no invented platform support or untested container access                                                                                                        |

The feature must not claim exhaustive causal attribution or treat it as a
software prerequisite. A reported override lock cannot establish that a physical
remote is an independently proven emergency stop. Live-service guest loss and
controller/OS failure must remain separate qualification cases.

A timer, average latency, write acknowledgement or manual power cut cannot prove
automatic independent stopping. If stock firmware cannot meet that boundary,
physical release must remain blocked pending an explicit scope decision; no
software timeout may substitute for an interlock. Requirements authorize no
device operation, firmware update or sibling-checkout edits.

## Source and reuse boundaries

The source evidence above is inspection evidence, not an imported dependency.
The reference browser application at `e65032ba2ed9c5709d91e5afd4f7034d7965fa32`
is GPL-3.0; its bundled BLE client is Apache-2.0. Neither application code nor a
mechanical port is selected. Original application code is intended for
Apache-2.0. Dependency additions, source imports and licensing changes require
approval.

Earlier source findings inform DC conformance, not a retained JavaScript defect
backlog: disabled knob limiting (DC-01), interleaved transitions (DC-04),
unawaited navigation/initialization failure fallthrough (DC-01), listener
identity and recursive disposal (DC-07). Any defect in retained code must have
an intended failing regression before its fix; source inspection is not
executable proof.

[firmware-version]:
  https://github.com/KinkyMakers/OSSM-hardware/blob/4871a84c54ae0734b1f8bc3d02fcc0098f12fbad/Software/src/constants/Version.h
[firmware-pairing]:
  https://github.com/KinkyMakers/OSSM-hardware/blob/4871a84c54ae0734b1f8bc3d02fcc0098f12fbad/Software/src/services/communication/pairing.hpp
[firmware-pattern-metadata]:
  https://github.com/KinkyMakers/OSSM-hardware/blob/4871a84c54ae0734b1f8bc3d02fcc0098f12fbad/Software/src/services/communication/patterns.hpp
[firmware-state]:
  https://github.com/KinkyMakers/OSSM-hardware/blob/4871a84c54ae0734b1f8bc3d02fcc0098f12fbad/Software/src/ossm/OSSM.cpp
[firmware-play]:
  https://github.com/KinkyMakers/OSSM-hardware/blob/4871a84c54ae0734b1f8bc3d02fcc0098f12fbad/Software/src/ossm/play_controls/play_controls.cpp
[firmware-ble]:
  https://github.com/KinkyMakers/OSSM-hardware/blob/4871a84c54ae0734b1f8bc3d02fcc0098f12fbad/Software/src/services/communication/nimble.cpp
[firmware-patterns]:
  https://github.com/KinkyMakers/OSSM-hardware/blob/4871a84c54ae0734b1f8bc3d02fcc0098f12fbad/Software/lib/StrokeEngine/src/pattern.h
[firmware-mode]:
  https://github.com/KinkyMakers/OSSM-hardware/blob/4871a84c54ae0734b1f8bc3d02fcc0098f12fbad/Software/src/ossm/stroke_engine/stroke_engine.cpp
[firmware-engine]:
  https://github.com/KinkyMakers/OSSM-hardware/blob/4871a84c54ae0734b1f8bc3d02fcc0098f12fbad/Software/lib/StrokeEngine/src/StrokeEngine.cpp
