# Web control: Guest, Admin, API and service library

Give a local operator and one invited guest a consistent control experience,
with all application authority in one testable library and one device owner.

## Status and scope

**Status: draft for review, 22 September 2026.** This document replaces the
former core-composition and application specifications. Their retained
requirements remain obligations; the proposed project split is not a claim of
implementation or approval of every design detail.

The requester selected SignalR instead of REST and confirmed
`OSSMDotNet.Api` as the host name. The requested `OSSMDotNet.ControlService`
is a business-logic library, not another executable. This design covers those
two components and `OSSMDotNet.AdminWeb` and `OSSMDotNet.GuestWeb`.

The audience is application/UI implementers, device-library maintainers and the
operator configuring a one-box installation. Review should settle the ownership
split and the [open issues](#open-issues) before implementation. No dependencies,
deployment, hardware operation or code changes are authorized by this draft.

Related contracts are the [specification index](README.md),
[device execution requirements](device-control/spec.md) and
[adapter design](device-control/adapters.md). DC-01 through DC-09 remain owned
by the device specification. The [architecture view](../diagrams/architecture.html)
records the pre-split architecture, not implementation conformance; the target
responsibilities below describe the proposed change.

## Background and intended outcomes

Before the split, `OSSMDotNet.ControlService` was an executable. The host
responsibilities now belong to [`OSSMDotNet.Api`](../../src/OSSMDotNet.Api/Program.cs):
its [`ControlHub`](../../src/OSSMDotNet.Api/ControlHub.cs) delegates operations
rather than writing the device directly. Both web hosts use
`OSSMDotNet.ControlClient` and `/hubs/control/v1`; there is no REST control API.

The pre-split application/execution separation was incomplete: invitation,
admission and guest-input operations lived in
[`ControlEngine`](../../src/OSSMDotNet.ControlCore/ControlEngine.cs).
[`ControlAuthority`](../../src/OSSMDotNet.ControlService/ControlAuthority.cs)
now owns that application state. These source observations do not establish
full design conformance.

The intended outcomes are:

- Operators can invite, approve, limit and stop a guest without understanding
  transports or assembling credentials.
- Guests see only their allowed controls and accurate, scoped feedback.
- Application rules can be tested without Kestrel, SignalR or a browser.
- Device execution remains independently usable and never trusts UI state.
- The split preserves wire behavior, bounded work and fail-closed recovery.

Non-goals are REST endpoints, another service process, accounts, persistence,
multiple devices/guests, automatic remote exposure, a plug-in framework, a new
command bus, a device-library rewrite or new physical qualification. Existing
command-line consumers remain consumers of the service contract; their UI and
the standalone device diagnostic tool are not being redesigned.

## Scenarios

| Scenario | Action and observable result |
| --- | --- |
| Start locally | The operator runs `aspire run`. AdminWeb displays the validated invitation automatically. Device control starts disarmed at zero; page load and invitation retrieval do not arm it. |
| Admit and approve | A guest opens the invitation. Exactly one connection becomes pending. AdminWeb shows the waiting notice. **Allow control** approves that exact guest and arms only when stopped feedback and valid limits permit it. Motion still requires a new guest Send. |
| Send and edit | Send commits the selected tuple. Further slider edits remain drafts unless Auto send is enabled. The active browser can renew only explicitly sent values with fresh grants; status refresh never sends drafts. |
| Change limits | The operator applies speed/depth together. Compatible limits take effect without pausing or rearming; conflicting limits reject atomically with guidance to Stop and wait for stopped feedback. |
| Stop or replace | Guest Stop pauses without ending the invitation. Guest Leave vacates the slot without replacing the link. Operator **Stop and end session** and Cycle atomically revoke old authority and publish a replacement, even while old device I/O settles. |
| Lose a connection | Detected guest loss revokes the binding and requests Stop. An undetected stall cannot extend the accepted grant deadline. The guest sees the connection-lost alert and must reopen the invitation; reconnect never resumes motion. |
| Recover from a lock | Only the operator can request unlock against the current authority/lock revision. Success leaves paused; a separate arm and new guest Send are required. |

## Scope and ownership

**Must** denotes an obligation, **should** a recommendation, **may** permission
and **can** capability. **Must not** denotes prohibition; **cannot** describes
inability. Firmware facts remain descriptive.

### Proposed component boundaries

| Component | Owns | Must not own |
| --- | --- | --- |
| `OSSMDotNet.AdminWeb` | Loopback-only Blazor operator pages, drafts, UI actions and an operator-scoped service client | Invitations, policy decisions, controller/device instances or native permissions |
| `OSSMDotNet.GuestWeb` | Blazor invitation redemption, guest presentation, browser sending state and a guest-scoped service client | Operator operations, approval, grants, policy changes or device access |
| `OSSMDotNet.Api` | ASP.NET Core process, listeners, host authentication/role enforcement, SignalR hub, strict wire validation, connection delivery and composition root | A second copy of application policy, session state or device scheduling |
| `OSSMDotNet.ControlService` | In-process application operations, invitation/session authority, policy versions, grants, expiry, scoped projections and coordination with the controller | ASP.NET Core, SignalR hub/context types, listeners, UI components or concrete adapters |

There must still be **three application processes**: AdminWeb, GuestWeb and
Api. ControlService runs inside Api. Api's host must own the lifetime of one
ControlService authority, one controller and one selected device. A hub instance
or browser circuit must not create its own authority or device.

The existing shared components have specific reasons to remain:

- `ControlClient` owns the remote boundary used by both web hosts and existing
  application clients. It must contain no authority or device execution.
- `WebCommon` owns the shared page layout and application widgets; RetroWebUi
  owns reusable widget styles and interaction treatments.
- `Models` carries immutable data, units and outcomes, not mutable authority,
  service implementations or native handles. Operation interfaces belong with
  the operation owner, not in the shared data assembly.
- `ControlCore` retains reusable execution safeguards. Its independent device
  consumer boundary is not replaced by an application-session service.
- `ServiceDefaults` and AppHost retain shared host configuration, telemetry and
  orchestration. They are not dependencies of the business or device core.

These supporting boundaries are retained, not a request for more assemblies or
a prescribed class/file inventory. Namespace and type organization must follow
their owner; public domain types must have XML documentation and one type per
file. External package namespaces must remain unchanged.

Today, `IControlOperations` is in `Models`, despite describing application
operations rather than data. Moving it to the operation owner is part of the
proposed extraction, subject to the public-type compatibility decision below;
this draft does not claim the current assembly already satisfies PST-03.

### Calls, state and lifetime

The guest browser communicates only with GuestWeb, and the operator browser
with AdminWeb. Each web host calls Api through its server-side ControlClient.
Browser-to-Blazor circuits and web-host-to-Api SignalR connections are distinct
boundaries. No service key is sent to a browser.

For an operation, Api authenticates the host and permitted role, validates the
wire shape, and supplies its authoritative connection identity to ControlService.
The library validates application authority, mutates the single serialized
session slot, and supplies trusted execution guards to ControlCore. The core
rechecks guards at execution and owns the single device writer. Replies report
acceptance, not physical completion.

For feedback, device observations flow through ControlCore to ControlService's
immutable, role-scoped projection. Api delivers that projection through the
ordered push path; ControlClient updates UI state without inventing authority.
Only Api owns SignalR connection objects and delivery subscriptions. The library
must accept connection-loss notifications as ordinary application operations
and expose revocation to Api without depending on transport types. Revocation
must become effective before transport closure and must not await an outbound
notification or guest acknowledgement.

The existing push dispatcher and wire-size checks belong in Api. ControlService
must determine which application transitions invalidate or supersede earlier
authority; delivery must consume that decision, not derive a second lifecycle
policy from presentation strings. Api owns connection bookkeeping and how those
decisions are delivered. This internal boundary requires no new wire fields.

One guest admission must remain bound to one authoritative Api connection.
GuestWeb must not share that binding between unrelated browser circuits. Browser
loss must stop its sending loop and notify the application when detected;
server timers must not keep its motion alive while its circuit is absent.

The library must own application deadlines and state transitions; Api must own
delivery/backpressure and the host lifetime. Slow delivery must remain bounded
and must not block Stop, expiry or rotation. Overflow must fail closed rather
than grow an unbounded replay queue. Exact queue implementation stays local to
the existing delivery boundary.

Components must use constructor injection and owner-provided
`IServiceCollection` registrations. Api selects the concrete adapter at its
composition root; the library consumes only the reusable controller contract.
The host owns asynchronous disposal. Request cancellation must propagate where
applicable, but accepted work and unsettled device I/O remain owned until their
outcome is known. A timeout or disconnect is not proof of rollback or settlement.

Arming and applying limits must use core operations that atomically check and
commit execution-side preconditions, not a snapshot read followed by a separate
write. ControlService must serialize the associated admission, policy and grant
transition around that outcome. Rejection must leave policy, grants and intent
unchanged. Lock acquisition must remain one-way from application authority into
the core; core code must not reacquire application authority while holding a
core lock. Publication and transport callbacks run outside those critical
sections. Splitting assemblies must not introduce a check-then-act gap or a
second copy of pending device state.

### Reusable core boundary

The controller, device abstractions, implementations and shared data must remain
usable without a web host, guest session, CLI or orchestrator. The application
supplies trusted authorization; the core enforces it at execution, including
after awaits and immediately before writes. The core must not infer authority
from UI state or implement invitation/guest policy.

| Unit | Permitted dependencies and responsibility |
| --- | --- |
| Device abstractions | No core/application implementation dependencies; own `IOssmDevice`, `DeviceState`, `MotionSettings` and device enums |
| Stock implementation | Device abstractions; shared adapter behavior, private protocol records and native connection ownership |
| Simulator implementation | Stock library's shared device base and abstractions; native engine integration and observation assessment |
| Shared models | Device abstractions where a core value is required; immutable data, explicit units and result/state variants only |
| Controller | Device abstractions and shared models; lifecycle, limits, scheduling, execution guards, Stop and lockout; never concrete adapters |

Stock and simulator adapters must satisfy the same observable contract, with
shared behavior in the stock library's device base. Stock must not depend on
simulation, and consumers must not branch on simulation. API composition may
select the implementation; that dependency must not reach the controller or
ControlService library. Device integration tests must work without application
hosts. The [adapter design](device-control/adapters.md#composition-and-ownership)
owns the implementation selection details.

Framework hosting, logging, options and timing libraries may support core
lifetime management. ASP.NET Core, SignalR, Blazor, Aspire and UI packages must
remain outside the core and ControlService business logic.

## Requirement ownership

IDs are retained for traceability, not new parallel versions of device rules.

| ID | Obligation or owning section |
| --- | --- |
| CC-01 | Start disarmed at zero; trusted owner authorization, valid limits and [DC-01](device-control/spec.md#dc-01-explicit-lifecycle-and-readiness) readiness precede arming. Guest approval is an application prerequisite. |
| CC-02 | [SESSION-01](#session-01-automatic-reusable-invitation): one memory-only reusable invitation and side-effect-free retrieval. |
| CC-03 | [Admission](#admission-and-session-lifetime): one connection-bound guest, private Join proof and explicit approval. |
| CC-04 | [Session lifetime](#admission-and-session-lifetime): bounded pending/approved leases; leave, loss and expiry revoke without rotating. |
| CC-05 | [Rotation](#invitation-rotation): atomic replacement, immediate availability and stale-authority rejection. |
| CC-06 | Limits follow [DC-03](device-control/spec.md#dc-03-capability-and-value-contract); application policy/grant behavior is in [Live operator limits](#live-operator-limits). |
| CC-07 | [Input freshness](#guest-input-and-grant-freshness): proof, versions, sequence, grants, rate limits and motion deadlines. |
| CC-08 | Ordered initial/live immutable snapshots preserve [DC-07](device-control/spec.md#dc-07-feedback-loss-and-disposal) observation semantics, pending I/O and unconfirmed outcomes, without secrets or native handles. |
| CC-09 | Scheduling/Stop follow [DC-04](device-control/spec.md#dc-04-one-writer-and-bounded-pending-work), [DC-05](device-control/spec.md#dc-05-stop-wins-without-concurrent-writes) and the [DC-02](device-control/spec.md#dc-02-narrow-consumer-surface) authority guard. |
| CC-10 | Unexpected-change detection and trusted-owner recovery follow [DC-09](device-control/spec.md#dc-09-external-parameter-changes-latch-an-admin-only-lockout); no less restrictive recovery path is permitted. |
| CC-11 | Faults, logical timeouts and disposal follow [DC-07](device-control/spec.md#dc-07-feedback-loss-and-disposal), retaining unsettled I/O ownership and never reviving motion. |
| PST-01 | Source/test organization matches namespaces and ownership. Logical requirements survive project renames; this design's target host/library names do not rename external packages. |
| PST-02 | Host defaults own shared host configuration, local HTTP protection and capability-token helpers; the reusable core must not depend on host support. Application capability decisions remain in ControlService. |
| PST-03 | Models are pure data. Clients and orchestration remain separate from models/controller; clients cannot become alternate device owners. |
| PST-04 | Device abstractions and implementations remain independently buildable/consumable with no controller, application-model, service or UI dependency. |

DEP-01, SESSION-01/02 and OUI/GUI IDs are defined in their sections below.

## Deployment and trust

### DEP-01: Local one-box operation

All three application processes, browser and device adapter must be able to run
on one supported computer without another server, proxy, tunnel, public DNS,
cloud identity or internet access after installation. Runtime assets must be
local. Local and remote guests must use the same policy and execution path.

AdminWeb must bind exclusively to `127.0.0.1`, remain deliberately
unauthenticated and require neither an operator token nor access timeout. It
must retain exact Host/Origin checks and applicable CSRF protection for mutations
and WebSockets. It must never be remotely exposed or proxied. Its listener must
use only `OSSM:Admin:AdminPort`, reject `Kestrel` and `PreferHostingUrls`
configuration before startup, ignore hosting URL/port overrides and disable
endpoint-configuration reload.

HTTP/WS is permitted only at the allowed loopback boundary. Remote guest and
service connections must use explicitly configured HTTPS/WSS with certificate
validation intact. The application must not automatically bind all interfaces,
trust forwarded headers, change firewall rules or start a tunnel. Containers
are optional; when authorized, rootless Podman must not require privileged mode
or an engine socket for device access.

### Local bootstrap root of trust

The operator must use `aspire run`. AppHost must own build, startup, port
allocation, health and shutdown; there must be no custom service launcher.
AppHost must start Api rather than the former ControlService executable once
the split is implemented. In simulator mode it prepares the native executable;
the Api-owned device adapter owns the only simulator child. Physical mode must
neither build/launch that child nor require Rust tooling. AppHost and Api DI
must use the same startup selection.

AppHost must generate distinct 256-bit service keys for AdminWeb and GuestWeb
and supply them through resource configuration to Api and the corresponding
host. Api authenticates hosts, not humans; ControlService must independently
validate guest capabilities. The authenticated local dashboard must display
the plain local operator URL. Keys must never be persisted by application code,
placed in process arguments, logged or exposed to browsers.

Typed configuration must use options, DataAnnotations, cross-field validation
where needed, `ValidateDataAnnotations()` and `ValidateOnStart()`. Shared host
defaults own common startup and OpenTelemetry configuration. Directly
constructed settings must also validate their invariants.

Processes under one OS identity are not isolated from arbitrary compromised
code with that identity. Local code can reach the unauthenticated operator
listener; service keys and separate listeners do not prove human intent or OS
isolation. Hardened remote deployments require distinct process identities and
native permissions. GuestWeb must not access privileged bootstrap storage or
native device permissions. A compromised guest host can forge input within a
capability it possesses, but cannot bypass service policy, grants or core limits.

Api must exclusively own controller/device lifetime. Shutdown must target only
its managed resources, never unrelated processes or a browser callback.
Api's composition root must retain the existing cross-process device-ownership
lease, using the same exclusion scope across the host rename. A competing host
must fail before starting device access; the owner must retain the lease through
managed device shutdown. One singleton per process alone is insufficient.
Physical-use limits remain in the
[qualification contract](device-control/spec.md#physical-qualification).

### SESSION-02: Accountless access

The application must not require accounts, registration, login, passwords,
personal identities, a user database or an external identity provider. Operator
is a local management role; guest means scoped capability possession, not a
verified identity.

Only ControlService may create guest capabilities. Session secrets must be
independent 256-bit CSPRNG values with fixed expiries and server-enforced scopes.
Guest credentials must not authorize invitation creation, limits, device
selection, approval, arming, unlocking or management.

Invitation secrets must travel in URL fragments and be removed promptly on
redemption. They must never enter query strings, request logs or retained browser
storage; subsequent credentials remain in memory. Validation retains hashes
only, except the bounded in-memory invitation result needed for repeat retrieval.
Telemetry, assets and source must contain no secrets. Restart discards all guest
authority; there is no session backup or restoration path.

## Invitations and sessions

### SESSION-01: Automatic reusable invitation

After advertised-origin validation, ControlService must create one high-entropy,
non-expiring invitation in memory. AdminWeb must automatically show its complete
URL with Copy and selectable fallback text. Retrieval, reload and compatible
creation retries must not change authority, write the device or create extra
invitations.

`GuestAdvertisedOrigin` must be one startup-validated canonical scheme/host/port
origin. User information, paths, queries and fragments must reject. Only literal
`localhost`, `127.0.0.1` and `[::1]` may use HTTP; all other hosts require HTTPS.
The configured origin must match GuestWeb's advertised listener origin before
publication. Missing/mismatched setup produces unavailable status, not a fallback
link. URLs must not be derived from request Host headers or the operator origin.

The invitation is `<guest-origin>/join/<sid>#invite=<secret>`, preserving scheme
and port. Loopback links must say **Local only - this computer**; remote links
require already configured reachable HTTPS exposure. The UI must explain missing
setup rather than present localhost as remotely shareable.

The serialized slot contains the current invitation generation and at most one
guest lease: `vacant`, `pending` or `approved`. These admission states are
separate from device readiness, pause, fault and lock states. A bounded
compatibility attempt ledger may associate retrieval attempts with their
generation, but must not create/replace invitations or resurrect old generations.
Compatible retries return the current snapshot or reject after rotation/restart.

Application operations `GetInvitation` and `CycleInvitation` retain `sessionId`,
`url`, `localOnly` and null `expiresAt`. The link's session ID is stable until
rotation; each admitted guest has a separate session instance. These logical
operations do not require new hub methods: invitation data uses the existing
operator projection and Cycle command.

### Admission and session lifetime

Join must atomically validate the current generation/proof and move a vacant
slot to pending. Concurrent joins admit exactly one connection. Join returns
only a private connection-bound bootstrap admission proof, not a snapshot or
grant. Every guest operation must validate proof, session, generation and the
authoritative Api connection binding, whether the proof is carried explicitly
or resolved from that binding.

Pending sessions expire after 60 seconds; approved sessions after 30 minutes.
Leave, expiry and detected transport loss revoke proof/grants, clear input,
request core Stop and vacate the slot without replacing the invitation. Expiry
must close the guest client connection. Internal session end has the same
non-rotating semantics as guest Leave and must not wait for acknowledgement.
A new admission requires fresh proof, approval and arming.

### Invitation rotation

Cycle and operator **Stop and end session** must be one atomic application
operation: advance generation; revoke the old link, proof, connection and grants;
clear pending input and stale authority-restoring requests; request serialized
core Stop; publish the replacement. Separate end/read calls are not equivalent.

The replacement must be available while old I/O settles, but outstanding I/O
must still prevent arming. Rotation leaves ordinary control paused and preserves
faults/locks. Late work and compatibility retries must not affect a replacement
guest or revive the old invitation. Restart also replaces the invitation.

### Approval and control authority

ControlService must approve the exact admitted guest before arming. **Allow
control** explicitly approves and prepares/arms only the stopped session when
fresh supported-profile feedback and limits satisfy CC-01. Opening pages,
retrieving/redeeming invitations and sending guest input never constitute
approval. Speed/depth limits default to 100%; editing them is optional. Physical
profiles remain subject to qualification.

Approval, arming and unlock must require the expected current authority revision
from DC-02. Guest operations cannot unlock. Successful trusted unlock follows
DC-09 and leaves paused; separate arm and a new Send precede motion. A fresh
invitation or session cannot bypass a fault or lock.

### Live operator limits

Limits must follow CC-06/DC-03. Compatible changes preserve approval, epoch and
last accepted sequence. Changed policy increments `PolicyVersion`, invalidates
old-policy grants and immediately publishes a new-policy grant in the same
epoch, without extending the accepted motion lease's original deadline.
Identical limits are a no-op.

Conflicts reject atomically as `limits-conflict`; old-policy inputs reject as
`policy-invalid`; superseded grants under current policy reject as
`grant-invalid`. Rejection must not change intent or sequence. Clients
resynchronize without dispatching unsent settings. A stale-policy retry may
retain explicitly sent settings, never extend motion through status refresh.
Operators may Stop and wait for stopped feedback before applying conflicting
limits. Apply must not pause, rearm or revive expired authority, and its hub
reply is acknowledgement-only.

## Service transport

SignalR at `/hubs/control/v1` remains the only application control transport.
The host split must not introduce REST, named-pipe control, browser-to-Api
access or an alternate device writer. Existing client method names, wire
representations and error meanings must remain compatible; moving projects is
not a reason to change the protocol.

Compatibility does not grandfather behavior that violates a retained safety or
authorization requirement. Characterization must distinguish contract-preserving
packaging changes from existing defects. Any fix that changes supported client
behavior needs a separately reviewed compatibility decision, not an undocumented
change hidden in the extraction.

| Caller | Allowed operation family | Result path |
| --- | --- | --- |
| AdminWeb service role | Subscribe operator state/logs, read invitation, Stop, Cycle/operator End, approve/Allow control, arm, apply limits, recover/unlock | Mutations acknowledge acceptance/rejection; invitation, state and safe logs arrive through ordered operator push |
| GuestWeb service role before admission | Stage subscription and Join using invitation proof | Join returns private admission proof only; no grant or state in the reply |
| Bound admitted guest | Scoped status, complete input, Stop and Leave | Acknowledgements plus the single authorized guest push path |

Guest exposure must exclude raw commands, enumeration, arbitrary invocation,
position streaming, firmware actions and simulator fault controls. Any retained
simulator fault-injection facility is privileged operator/test-only, not part
of the guest contract or ordinary device API.

Logical envelopes use closed explicit JSON schemas, with no CLR type-name
dispatch. Unknown/duplicate fields, inappropriate payloads or operations, and
unsupported versions must reject before application handling. Every inbound or
outbound logical envelope, including a bounded log batch, is at most 16 KiB of
serialized JSON, excluding SignalR framing. Guest-host parsing and quotas are
additional checks, never the authorization boundary.

Initial and live state must share one ordered server-push path. The in-session
cursor orders state/logs; it is not a resume or replay token. A timer pass that
does not change the guest-visible projection must not advance its publication
revision or queue another delivery. Disconnect must not replay, reconnect or
rearm guest control; clients begin a new admission flow.

Acknowledgement means application acceptance/rejection, not a settled write or
physical result. Lost replies and cancelled calls can have unknown outcomes;
clients must resynchronize rather than automatically retry authority-changing
commands. Only documented side-effect-free invitation retries and the guarded
explicitly-sent input retry above are permitted by this design.

Published schema versions retain units, interpretation and errors. Added fields
require a new supported version or explicitly negotiated closed-schema
extension. Participants must establish a common contract and required
capabilities before arming. Incompatibility fails closed without weakening Stop,
freshness or authorization. No speculative compatibility framework or assumed
simultaneous upgrade is introduced.

### Guest input and grant freshness

Version-2 input must contain `v = 2`, session/connection proof, control epoch,
policy version, grant ID and grant sequence, a strictly increasing positive
safe-integer `seq`, `run = true`, and a complete valid tuple. Api's connection
identity is authoritative. Version-1 hold inputs, nonincreasing sequences,
replays and expired/superseded/cross-session/epoch grants must reject without
writes or lease renewal.

ControlService issues an initial grant on explicit arming and one every 250 ms
while armed. Each contains session, connection, epoch, policy version, strictly
increasing grant sequence, issuance timestamp and expiry 750 ms after service
issuance, regardless of arrival. One unexpired grant may authorize increasing
input sequences. Accepted input supplies its original deadline and sequence to
the core motion guard; publishing a grant alone never renews a lease or moves
the device.

After authentication, at most 10 run-input attempts per connection/epoch may be
accepted in any rolling monotonic one-second window. Over-limit attempts must
not advance sequence, refresh authority, replace pending work or reach the
controller. Status, Stop and Leave do not consume that capacity; all retain the
envelope bound. The requester selected this rolling rule over the existing
20-capacity bucket; the [resolved decision](#resolved-direction-and-review)
requires a separately verified behavior correction, not a silent extraction change.

Session, policy and grant must be rechecked through the execution guard.
Server/circuit timers, heartbeat, ping/pong, status, retained UI state and probes
must not renew motion authority. Detected loss requests Stop; undetected stalls
remain bounded by the accepted grant deadline. One-second/three-second loss
thresholds are detection tuning, not real-time or physical-stop guarantees.
Restart discards sessions, proofs, sequences and grants; cached client state
must not restore them.

## User interfaces

Both hosts must retain Blazor Interactive Server, shared `ControlLayout`, page
header/content template, control sizing, spacing and notification components.
Presentation and browser state never authorize motion. Use native semantic
controls with labels, keyboard operation and meaningful status announcements;
color alone must not communicate authority, failure or availability.

### Guest interaction and feedback

The guest page must provide three vertical sliders, Send, opt-in Auto send and
Stop. Valid invitation links Join automatically; invalid/cycled links show only
the standard error alert, without a redundant error heading. Send commits the
selected tuple; Auto send commits changes only while enabled.

An active, visible browser may renew only its last explicitly sent tuple, no
more often than every 100 ms. Only one submission may be outstanding; newer
selections coalesce. Unsent drafts never enter a lease. Stop, focus/visibility
loss, connection loss and a new epoch disarm sending and clear Auto send.

Projections must preserve selected, sent and observed values, core observation
time and freshness. Missing data is unavailable; stale data is last-known.
Writes and liveness probes are not fresh feedback. `VerifiedSeq`, where retained,
associates settled writes with matching observations, not firmware IDs or
exclusive causation. Reported speed zero means operationally stopped; positive
means running. Do not require Enabled/Moving or claim physical settlement.
Simulator velocity may be displayed directly where that view permits it; stock
estimates/previews must be labelled and stale data must not animate as live.

### Shared notifications and styling

Application notifications must be normal-flow, full-content-width above the
widgets, without a fixed wrapper, popup, modal or outer widget border.
Framework-managed connection/reconnection/fatal notices are the only fixed
top-right notices and must remain outside interactive-route DOM. Connection
failures take precedence over other notices.

On guest connection loss, show the standard danger `RwAlert`: **Connection lost.
Open the invitation link again.** Grayscale the remaining guest interface and
hide paused guidance. A private operator live log must contain only bounded,
explicitly tagged safe application events, never exception details, scopes,
credentials or framework traffic.

Every button must have an accessible name and useful tooltip; icon-only buttons
need explicit labels, never an HTML `alt` attribute. Adjacent inputs/buttons
must match heights. Waiting is warning/gold; connection/success actions are
green; **Stop and end session** is warning/yellow; disabled buttons are
grayscale. Copied text and temporary success styling share one timer.

Shared widget styling changes belong in RetroWebUi and must be consumed through
the reviewed private package with checksum and CSP hashes verified together.
Applications must not patch copied assets or duplicate shared widget states.

### Operator presentation requirements

| ID | Requirement |
| --- | --- |
| OUI-01 | Feedback is collapsed by default and labelled **Live feedback**, without a redundant connection word. The action card heading remains **Controls**, not a transient readiness state. |
| OUI-02 | Above the narrow breakpoint, Controls and **Invite a guest** are adjacent equal-width/equal-height cards. Narrow layouts stack in source/keyboard order without horizontal overflow. The page heading aligns with the widgets' left edge. |
| OUI-03 | The shared site label is 1.4rem. SIMULATOR, when present, aligns at the page header's top-right with the label/widgets, not in Controls. The Controls guest-session indicator is right-aligned. Pending device I/O adds a preceding large recording-red dot with accessible text/tooltip **Waiting for the device to settle.**, not a normal-flow status message. |
| OUI-04 | **Session limits** spans the grid, with dirty status top-right. All operator/guest card headings render at 1.1rem without changing semantic levels; the limits landmark remains h2. |
| OUI-05 | Speed/depth share one right-aligned **Apply limits** action and atomic policy operation, without selected/applied text rows. A primary/selected-colour phantom dot marks applied track position while drafts differ and disappears on equality after successful application. Refresh preserves unsent edits. |
| OUI-06 | Both interfaces share the header/content template and inline notifications. An actionable pending guest shows persistent warning/gold **A guest is waiting. Click Allow control to enable control.** and green attention treatment on enabled Allow control. It clears when no longer actionable. The shared package supplies animated green gradient/glow, static reduced-motion treatment and disabled grayscale; readiness and safety checks still govern availability. |

### Guest presentation requirements

| ID | Requirement |
| --- | --- |
| GUI-01 | The shared main region has no initial visible focus outline; keyboard focus remains visible when used. The status card is **Live Status**, with state and applicable SIMULATOR badges in its right-aligned heading group and no numeric position, velocity or source-caption rows. |
| GUI-02 | Non-wrapping **Auto send** immediately precedes green **Send**; Auto send, Send and Stop are right-aligned together. Paused uses warning/gold without duplicate operator-disabled guidance. Terminated sessions say **Session ended. Open the invite link again to rejoin.** |
| GUI-03 | Invitation-required, interactive guest and operator routes share the page layout and standard inline notification, with normal-flow full-width content and no fixed application-notification container. |

## Decisions and alternatives

| Decision | Strongest practical alternative | Deciding trade-off |
| --- | --- | --- |
| Retain SignalR; name the host Api | REST commands with a separate server-push/lifetime mechanism | The requester selected SignalR. It already supplies connection-bound admission and ordered live delivery; REST would require redesign without a stated user benefit. |
| Extract the business library from the executable | Keep the current executable with internally separated classes | The current seam reduces extraction work, but leaves policy coupled to an ASP.NET host assembly. A library makes the requested independent test/use boundary explicit, at the cost of moving code and references. |
| Retain ControlCore separately | Merge application/session policy and execution into ControlService | Standalone controller/device consumers must not acquire remote-session or host requirements. Keeping this boundary preserves that contract; it does not justify duplicating authority in both libraries. |
| Keep three processes, not four | Host everything together, or run the business library as another service | One process would remove required listener/role separation. A fourth adds a hop and failure boundary without a new owner. Api hosts the library and the sole device lifetime. |
| Retain the existing client and shared UI owners | Direct SignalR calls and copied widgets in each UI | Two real consumers justify these boundaries. They avoid duplicated transport behavior and widget ownership; no generic provider/transport framework is needed. |

The retained single writer, bounded pending work, execution guards, grants and
timeouts cover real device I/O and authority-loss failure modes. Removing them
would change behavior, not simplify packaging. Conversely, a new repository
layer, mediator, durable queue, database or parallel compatibility stack has no
consumer in this scope.

## Operations and privacy

Use .NET 10, nullable-aware C#, asynchronous disposal, typed validated options
and `TimeProvider` elapsed-time APIs. Wall-clock changes must not renew authority
or freshness. Reuse existing pinned packages; this design requests no new
dependency or external service.

The local operator observes host health in the authenticated Aspire dashboard
and application availability/faults in the UIs. Liveness must not imply device
readiness. Application logs use `ILogger<T>` and shared OpenTelemetry, with
bounded structured event names and fields. Startup/lifetime events are
informational; rejected authority, lost connections and device faults must be
distinguishable without logging raw messages, identifiers, capability URLs,
proofs or keys. One owning boundary logs an unexpected failure; the safe UI log
is an explicitly filtered projection, not an exception/telemetry dump.

No uptime or latency SLO is claimed. Existing capacity/deadline contracts are
one device/one guest, bounded envelopes/work, session expiries, the rolling input
limit and grant deadlines. Tests with a controlled clock and simulator must
measure those contracts; they do not establish a physical stopping time.

Application authority is memory-only. The design adds no persistent user data,
backups or external telemetry destination. Any configured diagnostic export
must have reviewed access/retention and exclude secrets before use; this draft
does not claim an existing retention policy. The split adds no new licensing
selection or clearance and does not replace physical qualification obligations.

## Delivery and verification

Implementation is subsequent work. The
[implementation plan](../plans/web-control-implementation.md) owns the ordered
tasks, prerequisites and focused acceptance evidence. Keep the hub path and wire
contracts stable during extraction; record existing contract failures and explicit
behavior corrections separately. The split is incomplete until standalone core
use needs no guest session and the application library needs no transport host.

Restart during cutover discards authority and starts paused/disarmed; it must
not migrate live sessions or resume accepted input. Reverting packaging likewise
requires stopping the managed owner first, not running two writers. No schedule,
performance gain or deployment authorization is implied.

### Implementation and verification

ControlService tests must exercise application authority without ASP.NET or
SignalR. Core tests use deterministic device and clock fakes; direct adapter
integration tests do not start application hosts. Test observable outcomes,
ordering, bounded work and resource ownership rather than private class layouts.
Use xUnit v3/MTP v2, AwesomeAssertions and FastMoq where useful, retaining direct
value tests and deterministic fakes. Native engine tests may use their native
runner. No browser or physical device is required for core conformance.

### Application conformance

| Boundary | Required evidence before declaring the design implemented |
| --- | --- |
| Application library | One admitted connection; proof/generation binding; exact approval; atomic rotation with unsettled I/O; session expiry; policy/grant invalidation without lease extension; stale callbacks cannot revoke replacement guests |
| Api and ControlClient | Service-role separation; closed schemas and envelope bounds; ordered initial/live push; unchanged projections do not republish; expired authority closes the guest connection; loss and cancellation do not revive motion |
| Local host | Wrong Host and missing/foreign mutation Origin reject; listener overrides cannot expose AdminWeb; a second authority host cannot acquire device ownership; one-box startup works without external networking |
| Controller/device | DC safeguards tested by reference: one writer, execution-time guards, Stop priority, unconfirmed I/O and paused recovery; adapter checks remain host-independent |
| UI/components | OUI/GUI contracts, responsive keyboard order and focus, reduced motion, send/draft separation, freshness labels, clipboard fallback, connection-loss precedence and restart behavior |
| Existing CLI consumers | Compatible service calls, help, invalid-input exit codes, redirected output and cancellation; no guest secrets in arguments or diagnostics |

Use the lowest test level that proves each behavior, with focused browser checks
for high-value cross-boundary flows. Tests must not predominantly validate Aspire
or other dependencies. Simulator fault injection stays operator/test-only.
Software conformance must never be presented as hardware safety.

## Open issues

| Question | Impact and options | Next step and blocking scope |
| --- | --- | --- |
| Which grant metadata must cross the wire? | Current DTOs lack the specified grant sequence and explicit timestamps; issuance-based expiry exists internally. Keep logical metadata internal where the contract permits, or introduce a separately versioned wire change where explicit fields are required. Input sequence is not grant sequence. | The project owner must reconcile the schema requirements before grant-conformance work. Compatible host/library extraction is not blocked; full grant-schema conformance remains unproven. |
| Which Join boundary returns the private proof? | The internal application result supplies a proof retained against the connection; the current hub reply is acknowledgement-only. Clarify the application/wire distinction rather than exposing a secret to satisfy ambiguous wording. | Clarify the requirement with the project owner before changing the Join schema. Preserve the existing bound proof validation and hub receipt during extraction. |

### Resolved direction and review

- **SignalR, not REST:** the requester changed the initial REST brief to retain
  SignalR. This keeps the existing live connection and authority semantics.
- **Host name:** the requester selected `OSSMDotNet.Api` instead of RestApi.
- **Library role:** ControlService is the requested business-logic library. The
  detailed extraction and retained core boundary remain subject to design review.
- **Public .NET moves (22 September 2026):** the requester selected coordinated
  repository-consumer updates without source/binary compatibility shims. This
  resolves the public-type migration question; SignalR compatibility still holds.
- **Rolling limit (22 September 2026):** the requester selected 10 authenticated
  run-input attempts in any rolling monotonic second, replacing the tested
  20-capacity bucket. The bucket's burst allowance is not equivalent. Prove the
  regression first and update its tests as a separate behavior correction.

Start review with the project owner on the scenarios and ownership split, then
seek focused device-boundary and UI feedback as needed. Keep comments and
resolutions with this document and its version history. An AI review or silence
is not approval; unresolved grant-schema and Join-boundary questions remain visible.
