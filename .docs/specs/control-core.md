# Simulator-first ControlCore

The user approved this direction on 14 September 2026 and the SignalR migration
on 16 September 2026. This document closes the review gaps for that bounded
current implementation, without approving physical use. The existing CC and
DC-01 through DC-09 IDs remain authoritative.

The revised UI uses three vertical sliders, manual Send and an opt-in Auto send
checkbox. Hold-to-run is removed. Valid browser invitations join automatically
on opening; invalid or cycled invitations display only an error. Operator
**Allow control** explicitly approves the pending guest and arms the session.
Default speed/depth limits are 100%; applying limits is optional. Opening the
application still cannot arm motion. The explicit Allow control action prepares
the initially stopped controller.

Operator and guest routes share one persistent page-header structure supplied by
`ControlLayout`; their labels differ, but their layout does not. Invalid or
cycled invitations use the standard error alert without a redundant error
heading.

## ControlCore requirements

These requirements describe the simulator-first control authority currently
implemented by ControlCore. They are derived from executable behavior and do not
qualify physical-device safety or production deployment.

| ID    | Requirement                                                                                                                                                                                                                                                                                                                                                                                                                     | Acceptance evidence                                                                                                                                                                                                                      |
| ----- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| CC-01 | The control authority shall begin disarmed with a stopped desired state. It shall arm only after an admitted guest has been explicitly approved, local limits allow control, and fresh feedback confirms a supported simulator profile is stopped.                                                                                                                                                                              | An attempt to arm before any prerequisite is met is rejected; a successful arm publishes an initial authority grant while desired speed remains zero.                                                                                    |
| CC-02 | The control authority shall keep one reusable, non-expiring invitation in memory for its lifetime. Reading or retrying compatible creation of that invitation shall not alter active control.                                                                                                                                                                                                                                   | The same invitation is returned until it is cycled; it has no expiry and no retrieval operation changes the snapshot.                                                                                                                    |
| CC-03 | The control authority shall admit at most one guest connection for an invitation at a time through ControlService's `/hubs/control/v1` hub. Join shall require the invitation proof and return only a connection-bound private bootstrap admission proof; every new guest session shall require explicit approval and arming.                                                                                                   | Concurrent joins yield exactly one admitted connection; Join returns no snapshot or grant; wrong, malformed, stale, or connection-mismatched proof cannot read status, send input, stop, or leave.                                       |
| CC-04 | The control authority shall expire an unapproved guest session after 60 seconds and an approved session after 30 minutes. Expiry, guest leave, and detected transport loss shall revoke guest authority and request Stop without replacing the reusable invitation. The 1-second and 3-second loss thresholds are tunable detection only.                                                                                       | Expired or disconnected guests cannot use the prior proof; a detected loss does not reconnect, replay or rearm; the same invitation can admit a fresh pending guest after cleanup.                                                       |
| CC-05 | The control authority shall allow the operator to cycle the invitation or end the operator session. Either action shall immediately revoke the prior link, guest proof, connection, grants, pending input and stale authority-restoring requests; request serialized Stop; publish the replacement invitation; and never arm or clear a fault or external-control lock.                                                         | Old guest operations and authority restores carrying a stale expected authority revision reject after Cycle, Stop or operator end; pending motion does not dispatch, and a new invitation is available while outstanding I/O settles.    |
| CC-06 | The control authority shall enforce an all-or-nothing local speed and depth policy. Compatible policy changes shall preserve current safe authority, intent, and lease deadlines while invalidating prior-policy grants; conflicting changes shall reject without modifying policy or control state; an identical change shall be a no-op.                                                                                      | A compatible policy change publishes a fresh-policy grant without rearming; stale policy or grant input rejects; any conflict with accepted, observed, pending, in-flight, or unconfirmed motion rejects atomically.                     |
| CC-07 | The control authority shall authorize motion only from an authenticated admitted guest using a current protocol version, epoch, policy version, grant, strictly increasing sequence, complete valid tuple, and available rate-limit capacity. Grants shall expire 750 milliseconds after issuance; renewal shall be driven only by explicit valid input, not status, policy updates, or grant publication.                      | Invalid inputs cause no device write or lease renewal; the eleventh authenticated run-input attempt in any rolling one-second admin-monotonic window rejects; an expired lease pauses control and requests Stop.                         |
| CC-08 | The control authority shall expose immutable state only through one ordered initial/live ControlService push path. Guest-visible state shall include only session-scoped authority and live position/velocity, and shall withhold raw device details and connection identifiers. Device settings shall remain unverified until matching fresh feedback arrives; stale feedback shall not qualify control.                       | Ordinary command acknowledgements are not state; initial and live state arrive in cursor order on the same path; stale or unsupported feedback prevents arming and clears guest-visible live movement.                                   |
| CC-09 | The control authority shall serialize device operations, keep only the newest replaceable motion intent, and give Stop precedence over input. It shall advance authority revision on Stop, and every authority-restoring operator action shall supply the expected current revision. It shall not dispatch a later motion command after Stop, expiry, lockout, fault, disposal, or a missed execution-time authorization check. | Rapid input produces bounded pending work; a delayed write settles before serialized Stop; stale authority-restoring requests reject; a pending envelope transition writes zero before later speed and rechecks authority between steps. |
| CC-10 | The control authority shall latch an external-control lock when fresh device feedback reports an unowned change to a controlled setting. Locking shall revoke authority, clear pending input, request Stop, and remain in effect until an explicit operator unlock using the current lock revision; unlock shall leave the controller paused.                                                                                   | Unowned changes during rest or in-flight writes lock control; stale unlock revisions fail; reconnect, invitation reuse, policy edits, recovery, and late input cannot clear the lock or restore motion.                                  |
| CC-11 | The control authority shall fail closed on device-operation timeout, bridge failure, unsupported feedback, or disposal. It shall retain exclusive ownership until outstanding I/O settles, request Stop where possible, and expose a non-ready state without invitation secrets.                                                                                                                                                | Late completion cannot revive motion; failed bridge work latches a safe reason and only Stop is queued; disposal revokes the invitation and unsubscribes device feedback.                                                                |

The sections below detail invitation, policy, transport, and safety behavior.
They refine these requirements and remain the source for endpoint and protocol
specifics.

## Live operator limits

**Apply limits** does not pause control. A compatible changed policy preserves
Ready, generation, epoch, last accepted sequence, approved session and
serialized I/O. The accepted desired tuple, active observed tuple,
pending/in-flight writes and issued commands awaiting feedback must remain
within the proposed bounds. Otherwise reject the entire change as
`limits-conflict`, leaving the previous policy and control state intact; do not
clamp, silently pause or auto re-arm. The operator can explicitly Stop and wait
for stopped feedback before applying bounds incompatible with current motion.

Each successful changed policy increments `PolicyVersion`; identical limits are
a no-op. In Ready, superseded grants are cleared and a fresh-policy grant is
published immediately with the same epoch and sequence. This does not renew the
accepted authority: existing motion retains its original 750 ms issuance
deadline until new explicit guest input is accepted. Apply/status cannot keep
motion alive, and an overdue lease cannot be revived by Apply.

Old-policy input rejects as `policy-invalid`; current-policy input carrying a
superseded grant rejects as `grant-invalid`. Neither changes accepted intent or
sequence. Clients resynchronize policy/status without automatically submitting a
new tuple. The limits operation is invoked through the ControlService hub,
returning an acknowledgement-only success or rejection code. Initializing,
paused, locked and faulted controllers never arm from Apply. Stop, external
lock, session expiry and existing faults keep precedence. An action that
restores authority supplies the expected current authority revision; Stop
advances that revision, so a request started before Stop cannot restore
authority afterwards.

## Operator presentation requirements

These presentation requirements make the operator's local state and policy
controls easier to scan. They do not change ControlService's ownership,
authorization, or safety checks.

| ID     | Requirement                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                      | Acceptance evidence                                                                                                                                                                                                                                                                                                                                                                                                                                 |
| ------ | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| OUI-01 | The collapsed feedback control shall be labelled **Live feedback** without a redundant connection-state word. The operator action card shall have the stable heading **Controls**, rather than presenting a transient readiness state as its heading.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                            | Before a guest joins, the page shows the Controls heading and the waiting guidance without a Connected label beside Live feedback.                                                                                                                                                                                                                                                                                                                  |
| OUI-02 | At widths above the narrow layout breakpoint, the Controls and **Invite a guest** cards shall be adjacent, equal-width cards in the same grid row and shall stretch to the same height. At narrower widths they shall retain that source and keyboard order while stacking without horizontal overflow. The page heading shall align with the widgets' left edge.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                | Browser checks confirm the desktop card geometry and title alignment, and narrow layouts have no horizontal scroll.                                                                                                                                                                                                                                                                                                                                 |
| OUI-03 | The `ControlLayout` site label shall be 1.4rem. When present, the SIMULATOR indicator shall appear at the page header's top-right edge, vertically aligned with the site label and the widgets beneath it; it shall not appear in the Controls-card heading. The guest-session indicator shall remain right-aligned in the Controls heading. While ControlService reports pending device I/O, a large recording-red dot shall precede that guest-session indicator. The marker shall expose the text **Waiting for the device to settle.** to assistive technology and as its tooltip; it shall not add a normal-flow status message.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                            | The Operator controls label and SIMULATOR indicator share the page-header row, with the latter aligned to the widgets' right edge. The guest session remains in the Controls heading. Pending I/O adds the accessible, tooltip-bearing red dot immediately before it without changing the card layout.                                                                                                                                              |
| OUI-04 | The **Session limits** card shall span the operator grid and retain its top-right dirty status. Every card heading slot across the operator and guest interfaces shall render at 1.1rem without changing semantic heading levels.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                | The limits landmark remains an h2, spans the combined width of the two cards above it, and all card heading slots render at 1.1rem.                                                                                                                                                                                                                                                                                                                 |
| OUI-05 | The speed and depth ranges shall share one right-aligned **Apply limits** action in their control row. Applying remains one atomic speed-and-depth policy operation. The ranges shall not render redundant selected/applied text rows or a textual applied value. While a selected value differs from the confirmed policy, its range shall show only a primary/selected-colour applied-value phantom dot at the applied track position; the dot shall disappear once successful application makes the values equal.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                             | Changing one range shows its applied-position phantom dot and the not-applied state without changing policy. A successful compatible Apply clears the dirty state and dot without pausing or rearming control.                                                                                                                                                                                                                                      |
| OUI-06 | Operator and guest application notifications shall use the same shared inline notification component. `ControlLayout` shall supply their shared page-header and normal-flow content template; pages may differ only by label text and their content widgets. Notifications shall be full-content-width items above page widgets; they shall not use a fixed notice wrapper, popup, modal, or outer widget border. While an admitted guest awaits approval and the safety checks make **Allow control** available, the inline notification shall show the persistent warning/gold message **A guest is waiting. Click Allow control to enable control.** The notice shall clear when the session is no longer awaiting approval or Allow control is unavailable. The enabled **Allow control** action shall use the shared attention treatment: a prominent green glow with an animated green background gradient. The treatment shall be static when reduced motion is requested, and disabled controls shall remain grayscale without attention animation. Framework-owned connection, reconnection, and fatal notices remain the only fixed top-right notices. | Component and browser checks show the common page-header and inline-notification classes, normal-flow content-width geometry, and absence of a fixed wrapper for the operator pending approval and guest session-ended messages. The shared `rw-attention` class appears only for an actionable pending guest. The shared widget package supplies the green animated treatment, a reduced-motion static treatment, and disabled grayscale behavior. |

## Project structure

| ID     | Requirement                                                                                                                                                                                                                                                                                                                                                                              | Acceptance evidence                                                                                                                                                                                                         |
| ------ | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| PST-01 | Local projects and their matching test projects shall not use the erroneous `OSSMDotNet.Web.*` namespace segment. ControlService, AdminWeb, GuestWeb, service-boundary tests, and their corresponding test projects shall use their matching `OSSMDotNet.*` namespaces; the shared web component project shall use `OSSMDotNet.WebCommon`. External package namespaces remain unchanged. | The solution, project references, local C# and Razor namespaces resolve without erroneous `OSSMDotNet.Web.*` project names; the external `OSSMDotNet.Web.RetroWebUi` import remains intact.                                 |
| PST-02 | Host configuration, local HTTP boundary protection, and capability-token helpers shall be owned by `OSSMDotNet.ServiceDefaults`. The separate HostRuntime project shall not remain.                                                                                                                                                                                                      | All consumers compile against ServiceDefaults, no HostRuntime project is present in the solution, and the existing local-boundary tests pass.                                                                               |
| PST-03 | The command-line project shall be named `OSSMDotNet.Cli`. Session wire contracts shall be owned by the pure `OSSMDotNet.Models` project rather than a separate SessionContracts project. The Aspire host shall be named `OSSMDotNet.AppHost`.                                                                                                                                            | The solution has no PrototypeCli, SessionContracts, or unqualified AppHost project entry; direct consumers compile through Cli, Models, and AppHost respectively.                                                           |
| PST-04 | The device bridge shall be an independently buildable and consumable library feature. `OSSMDotNet.DeviceBridge.Abstractions` shall own `IDeviceBridge`, `DesiredState` and `DeviceSample` directly in its root namespace, and `OSSMDotNet.DeviceBridge` shall own the implementation and private simulator protocol records.                                                             | Abstractions has no project or package dependency; DeviceBridge has no dependency on Models, ControlCore, ControlService, Aspire or a web host; direct bridge integration tests run without starting the application hosts. |

## Guest presentation requirements

| ID     | Requirement                                                                                                                                                                                                                                                                                                                                                                                                                | Acceptance evidence                                                                                                                                                                                                                                                                                 |
| ------ | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| GUI-01 | The shared page layout shall not leave an initial visible focus outline around its `main` region. The guest status card shall be headed **Live Status**, show the simulator indicator in its right-aligned heading status group when applicable, and omit numeric position, velocity, and source-caption rows.                                                                                                             | On initial guest-page load, main has no visible focus outline; Live Status contains the SIMULATOR badge and state badge, while position, velocity, and simulator-source text are absent.                                                                                                            |
| GUI-02 | The guest action row shall keep a non-wrapping **Auto send** immediately before the green **Send** action, with Auto send, Send and Stop right-aligned as one group. A paused guest status shall use the warning/gold style. It shall not duplicate the paused guidance with a separate operator-disabled message. A terminated session shall instruct the guest: **Session ended. Open the invite link again to rejoin.** | The enabled and disabled guest actions retain semantic button names and styling; their group is right-aligned and the Auto send label remains on one line; paused, waiting, and ended guest messages use the shared inline notification component; an ended session shows the exact rejoin message. |
| GUI-03 | The invitation-required guest page shall use the standard inline notification component in the same `ControlLayout` as the interactive guest route and operator route.                                                                                                                                                                                                                                                     | The invitation-required response uses the common page header and main frame with a normal-flow, full-width inline notification without a fixed notification container.                                                                                                                              |

## Existing simulator, not a second motion engine

Use the local sibling checkout `..\ossm-rs`, inspected at commit
`578161b2e78d8ca70f8909f0ec34b8ae6014cfda`. It is Apache-2.0 licensed. Its
`ossm-sim` application is an egui GUI that calls `ossm-motion` directly; it does
not expose a network/device endpoint.

`tools\ossm-sim-bridge` is a headless stdin/stdout adapter using the same
`ossm-motion` crate. It supplies an inert motor, timing and telemetry callbacks.
It must not open native device I/O, load GUI saved state, or modify the
`ossm-rs` checkout. `OSSMDotNet.DeviceBridge` starts this executable when given
its path and can be consumed directly without ControlService. ControlService
owns that child process when hosting the bridge in this application. The
production native-device implementation remains an unimplemented extension
point.

The profile is **`ossm-rs-sim-v1`**, not the stock firmware profile. OSSM-RS
uses its own motion mapping and may retract when disabled. The adapter reports
enabled state, actual simulated movement and settings separately. Stopped
settings do not imply settled simulated movement; failure to settle stays
paused/faulted. Nothing observed here qualifies stock firmware, the physical
knob or physical stopping latency.

The adapter's fault controls simulate external parameter change, delayed write
completion, frozen feedback and disconnection. Only ControlService operator
operations can invoke them. Command/sample correlation is explicit simulator
evidence; it must not be assumed to exist in a native adapter.

## Local bootstrap root of trust

The operator uses `aspire run` in their OS session. Aspire AppHost owns builds,
startup, port allocation, health and shutdown. It generates separate 256-bit
AdminWeb and GuestWeb service keys for ControlService. The keys authenticate the
calling host over HTTPS/WSS; they are not user credentials and do not replace
independent guest invitation/session-capability validation. Aspire resource
environment configuration replaces the earlier custom stdin launcher.

The authenticated local Aspire dashboard displays a plain local operator URL.
AdminWeb and the CLI have no operator token or access timeout. AdminWeb always
binds to `127.0.0.1` and retains exact Host/Origin checks, including Origin
checks for state changes and WebSockets. Never expose or proxy AdminWeb
remotely. Guest capability checks and leases remain enforced by ControlService.
The 15 September 2026 request removes invitation expiry only; it does not extend
guest control authority. AdminWeb rejects all `Kestrel` and `PreferHostingUrls`
configuration before startup. Its listener uses only `OSSM:Admin:AdminPort`;
hosting URL/port settings cannot replace it, and endpoint configuration reload
is disabled.

Treat Aspire development data and process environments as privileged.
Application code does not persist credentials, put them in arguments or log
them. Secret-parameter marking is not an OS isolation guarantee. Typed
`Microsoft.Extensions.Options` configuration uses DataAnnotations,
`IValidatableObject` and `ValidateOnStart`. Both UIs use Blazor Interactive
Server; AdminWeb, GuestWeb and the CLI call ControlService at
`/hubs/control/v1`. SignalR is the only current control transport. Application
operations preserve REST-compatible semantics for a future API, but no REST
control surface exists or is implemented. No retry handler renews commands.

This first local ControlCore deployment runs its processes as the invoking OS
user. Separately generated service keys, HTTPS/WSS, separate listeners and
operation allowlists protect the application boundary; **they do not isolate
arbitrary compromised code running as the same OS user**. AdminWeb is
loopback-only; GuestWeb and ControlService may be separate hosts. Distinct OS
process identities and native permissions are a prerequisite to a hardened
remote deployment, not a claim made by this ControlCore.

Only one admin instance may own the ControlCore device. Startup remains
disarmed. Stop/shutdown targets the processes managed by Aspire; it does not
kill unrelated processes or rely on a browser callback.

## Invitation creation and full-origin links

The invitation is automatically available on the admin page, without a Create
action. It has no expiry and is reusable throughout the admin application's
lifetime. Keep it only in admin memory; restart changes the link. Invitation
retrieval does not issue device writes or change an active controller.

The control authority owns one serialized invitation/session slot containing the
current invitation generation and at most one connection-bound guest lease in
`vacant`, `pending` or `approved` state. Startup creates the initial invitation
generation only after advertised-origin validation succeeds. Retrieval reads
that immutable generation without a state transition. Join compares the current
generation and invitation proof, then atomically moves a vacant slot to pending.
Leave, guest transport loss and session expiry revoke only the guest lease and
return the slot to vacant; they retain the invitation.

The invitation is separate from the active guest session. Join atomically
reserves the single guest slot and issues a fresh connection-bound proof; it
never admits a second pending or approved guest. Leave, transport loss, or guest
lease expiry revoke the old proof and follow Stop. The same invitation can then
join a new pending session. Pending approval still expires after 60 seconds;
approved sessions still expire after 30 minutes. Approval and explicit arming
remain required on every new session.

**Cycle link** atomically replaces the invitation, invalidates the old link,
guest proof, connection authority and grants, clears pending input and requests
serialized Stop. The replacement is available immediately, even while old I/O
settles. It cannot arm, clear an external lock or bypass outstanding-I/O checks.
Normal state is paused; existing locked/faulted states retain their protections.
The operator's **Stop and end session** action also atomically cycles the
invitation. Its ControlService command uses the same engine operation as Cycle,
not separate end/get steps. The replacement is available through the ordinary
invitation command immediately. Internal/native `EndSession` and guest Leave
retain their existing non-rotating semantics; only the operator application
action now rotates.

The application operations are `GetInvitation` and `CycleInvitation`. They are
called through the hub now and retain REST-compatible semantics for a future
API; no REST compatibility wrapper or endpoint is implemented. The invitation
result retains `sessionId`, `url`, `localOnly` and a null `expiresAt`. Its
session ID remains the link's ID until Cycle or operator end; every admitted
guest receives a separate session instance and fresh proof. Guest wire schemas
remain strictly versioned.

`GuestAdvertisedOrigin` is startup-validated typed configuration, not a request
Host header. It is one canonical absolute origin containing scheme, host and
port only: user information, path, query and fragment are invalid. Preserve
`http` only for the literal hosts `localhost`, `127.0.0.1` and `[::1]`;
otherwise require `https`. The admin validates that the configured guest
listener advertises this exact canonical origin before it publishes the initial
invitation. Failure leaves invitation retrieval explicitly unavailable rather
than returning a guessed or fallback link. Append
`/join/<session-id>#invite=<secret>` without prepending another scheme. Local
links are labelled local-only. ControlCore defaults to loopback and does not
configure remote routing, certificates or a tunnel.

## Guest transport and freshness

AdminWeb, GuestWeb and CLI use ControlService's SignalR endpoint
`/hubs/control/v1` over HTTPS/WSS. AdminWeb and GuestWeb authenticate their
server-host connections with separately generated service keys. GuestWeb sends
the guest proof with its guest operations; ControlService independently
validates the proof, session and connection binding. The allowed guest
operations are `join`, `status`, `input`, `stop` and `leave`, not management,
device selection or simulator controls. There is no named-pipe, IPC or REST
control path.

Hub commands use closed, versioned JSON logical envelopes. Each inbound and
outbound logical message is at most 16 KiB of serialized envelope JSON,
excluding SignalR and WebSocket framing. Ordinary command replies are
acknowledgement-only: they report acceptance or rejection and never substitute
for device confirmation or state. Join returns only its private connection-bound
bootstrap admission proof. Initial state and all subsequent live state use one
ordered server-push path. Its cursor orders state and bounded log batches only;
it is not replayable or usable to resume a connection.

The run-input schema is version 2: an input includes `v = 2`, session/connection
proof, a control epoch, policy version, grant ID and grant sequence, strictly
increasing positive safe-integer `seq`, `run = true`, and a complete desired
tuple. The hub-bound connection identity is authoritative; per-message client
fields cannot substitute for it. ControlService rejects a sequence less than or
equal to the connection and epoch's last accepted sequence, an
expired/superseded grant, a cross-session/epoch grant or replay. Version-1 hold
inputs are rejected. Closed schemas reject unknown fields, inappropriate
payloads and unsupported operations.

ControlService creates an initial grant when a stopped session is explicitly
armed, then issues grants every **250 ms** while that epoch remains armed. Each
grant records the session, hub-bound connection, epoch, policy version, strictly
increasing grant sequence, issuance timestamp and expiry **750 ms from
ControlService issuance**, irrespective of arrival. The fixed 750 ms grant is
independent of the tunable 1-second/3-second transport-loss detection settings.
The same unexpired grant can authorize increasing sequence numbers. The
controller copies the accepted input's grant deadline and input sequence into
its motion lease. Issuing or publishing a later grant does not replace that
lease, renew motion or dispatch a write: only accepted fresh guest input
authorizes output, until that input grant's original deadline.

ControlService applies a rolling admin-monotonic one-second input window after
guest authentication. It accepts no more than **10** run-input attempts for a
connection and epoch in that window. An over-limit attempt rejects without
advancing the accepted sequence, refreshing a grant, replacing pending work or
reaching the controller. Status, Stop and leave do not consume that input
capacity; all logical messages retain the 16 KiB budget. Browser Auto send may
coalesce input but cannot relax this service limit. No heartbeat or status
request renews motion. The existing 10-attempt rolling-second requirement and
the 20-capacity bucket remain inconsistent and unresolved; this migration does
not change either requirement to hide that mismatch.

Send commits the selected tuple. Auto send commits slider changes while enabled.
An active, visible browser renews only its last sent tuple at most every **100
ms**; unsent edits never enter that lease. At most one submission is
outstanding; auto changes coalesce to the newest selection. Stop,
focus/visibility loss, connection loss and new epochs disarm sending and clear
Auto send. Server/circuit timers read status only; they never manufacture run
input. A timer pass that leaves a guest-visible projection unchanged shall not
advance its publication revision or queue another state delivery.

Selected values, transmitted values and device-confirmed current values are
distinct. A hub acknowledgement is not device verification. ControlService
publishes safe current settings/position/velocity from fresh device samples
while keeping raw device details private. `VerifiedSeq` advances only after the
matching owned device command is settled and positively observed. Missing/stale
feedback removes current telemetry instead of animating guessed movement. The
run indicator uses the reported enabled state or ongoing movement, so stroke
turnarounds do not flicker idle. This display rule does not change stopped-state
checks. Operator feedback is collapsed by default. A private, bounded live log
displays only explicitly tagged, safe application activity; exception details,
scopes, credentials and framework traffic are excluded.

ControlService rechecks session, generation, lock, policy and grant at
execution. Detected transport loss, expiry and Stop revoke authority and clear
pending input. Stop advances authority revision and invalidates any in-flight
authority-restoring operator request whose expected revision is now stale. A
delayed issued write remains owned until it settles; timeout is not
cancellation. DC-09 external changes lock until explicit admin unlock, which
leaves paused and requires separate arming. ControlService status explicitly
reports pending I/O; readiness is not inferred from zero settings alone. Guest
authorization expiry closes its client connection. The 1-second/3-second loss
thresholds are detection tuning only, not safety guarantees, real-time deadlines
or wall-clock clock synchronization. There is no reconnect, replay or rearm
path.

## Current implementation acceptance

- Aspire starts AdminWeb, GuestWeb and ControlService with the Rust simulator,
  reports availability and owns shutdown. System.CommandLine supplies the
  optional ControlService client, not a second orchestrator.
- ControlService can initialize, set limits, retrieve the automatic reusable
  invitation, approve the exact guest, arm, stop, revoke and unlock without
  accounts. AdminWeb and GuestWeb use the versioned SignalR hub with their
  distinct service keys; AdminWeb stays unauthenticated and loopback-only.
- Guest can join, view live state, send restricted settings and request Stop,
  but cannot reach privileged operations or the simulator directly.
- GuestWeb/control transport loss expires authority without reconnect, replay or
  rearm. External changes lock; delayed and stale simulator behavior cannot
  reopen motion or create a second writer.
- .NET tests use xUnit v3, MTP v2 and FastMoq where appropriate; deterministic
  I/O and clock tests remain explicit. Rust uses its native test runner.
- Native BLE, physical stopping and remote-production privilege isolation remain
  excluded until separately qualified. The 1-second/3-second loss detection and
  750 ms grant are not a safety claim or hardware qualification.
