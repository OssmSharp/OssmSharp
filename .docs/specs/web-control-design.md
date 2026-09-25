# OssmSharp: direct-device CLI and web control

Give a local operator and one invited guest a consistent control experience,
with all application authority in one testable library and one device owner.

## Status and scope

**Status: accepted architecture, 25 September 2026; migration not implemented.**
The requester accepted the single-computer direct-device model and an
interactive CLI, replacing the earlier separate device-service proposal. The
device user is the local admin; the MVP has one device and one remote guest.
Repository, project, assembly and namespace identities move from `OSSMDotNet`
to `OssmSharp`. This is a WIP migration with no backwards-compatibility shims.

`OssmSharp.Web.Api` is the device-owning web backend, not a gateway to another
device service. `OssmSharp.Cli` is an alternative direct owner of the same
device. The machine-wide lock makes these ownership modes mutually exclusive.
Both web UIs remain separately runnable processes. API restart also restarts
web device ownership; that shared lifetime is an accepted MVP trade-off.

The audience is application/UI implementers, device-library maintainers and the
admin configuring the computer. The detailed choices below close the design
questions; physical/platform claims remain subject to evidence. Only design
documentation and migration planning are authorized now, not implementation,
dependencies, deployment or hardware operation.

Related contracts are the [specification index](README.md),
[device execution requirements](device-control/spec.md) and
[adapter design](device-control/adapters.md). DC-01 through DC-09 remain owned
by the device specification. The [architecture view](../diagrams/architecture.html)
shows the accepted target, not implementation conformance.

## Background and intended outcomes

At the inspected migration baseline, host responsibilities belong to
[`OSSMDotNet.ApiService`](../../src/OSSMDotNet.ApiService/Program.cs):
its [`ControlHub`](../../src/OSSMDotNet.ApiService/SignalR/ControlHub.cs) delegates operations
rather than writing the device directly. Both web hosts use
`OSSMDotNet.ControlClient` and `/hubs/control/v1`; there is no REST control API.

The pre-split application/execution separation was incomplete: invitation,
admission and guest-input operations lived in
[`ControlEngine`](../../src/OSSMDotNet.ControlCore/ControlEngine.cs).
[`ControlAuthority`](../../src/OSSMDotNet.ControlService/Authority/ControlAuthority.cs)
now owns that application state. These source observations do not establish
full design conformance.

The intended outcomes are:

- Operators can invite, approve, limit and stop a guest without understanding
  transports or assembling credentials.
- Guests see only their allowed controls and accurate, scoped feedback.
- Application rules can be tested without Kestrel, SignalR or a browser.
- Device execution remains independently usable and never trusts UI state.
- The CLI works with all web hosts and AppHost absent, without web policy.
- Provider-specific packages are excluded from deployments that do not use them.
- Configured logging and telemetry work without Aspire or a collector.
- The migration preserves bounded work and fail-closed recovery.

Non-goals are REST control endpoints, a standalone ControlService, a device
service client in the CLI, accounts, persistent sessions, multiple
computers/devices/guests, automatic remote exposure, a plug-in framework, a new
command bus, another motion engine or physical qualification in this migration.

## Scenarios

| Scenario | Action and observable result |
| --- | --- |
| Start locally | Start the three web executables directly or use the optional simulated AppHost arrangement. AdminWeb displays the validated invitation automatically. Device control starts disarmed at zero; page load and invitation retrieval do not arm it. |
| Use the software remote | Start the simulator independently, then start Cli with provider options. One prompt retains the device connection and ownership lock across help, status, motion, Stop and quit. No web process or guest session exists. |
| Compete for ownership | CLI or Web.Api fails before device access when the other holds the machine-wide lock. It never steals ownership or silently starts another simulator. |
| Exit the CLI | Quit, EOF, Ctrl+C and handled shutdown inhibit new motion, prioritize a bounded awaited Stop, disconnect and release the lock last. Report unconfirmed Stop rather than successful stopping. |
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

### Target component boundaries

| Component | Owns | Must not own |
| --- | --- | --- |
| `OssmSharp.Cli` executable | Interactive direct-device remote; validated commands; one connection and ownership lifetime | Web policy/controller, sessions, authentication, UI hosts or simulator process startup |
| `OssmSharp.Web.AdminWeb` executable | Loopback-only admin Blazor UI, drafts and operator-scoped Web.Client | Authority, device instances or BLE permissions |
| `OssmSharp.Web.GuestWeb` executable | Guest Blazor UI and guest-scoped Web.Client | Operator methods, policy or device access |
| `OssmSharp.Web.Api` executable | SignalR endpoint, host-role enforcement, strict wire validation, delivery and composition of one application/controller/device lifetime | UI components or a second authority/writer |
| `OssmSharp.Web.Application` library | Web sessions, invitations, grants, policy, projections and execution controller; absorbs ControlService and the web-policy controller from ControlCore | ASP.NET/SignalR contexts, UI, concrete adapters or the CLI |
| `OssmSharp.Web.Client` library | Server-side SignalR client used only by the two web UIs | Device implementations, authority or backend startup |
| `OssmSharp.Web.Contracts` library | Web DTOs, request/result values and error contracts | Mutable authority, clients, hosts or UI components |
| `OssmSharp.Web.Common` library | Shared Blazor layout/widgets using RetroWebUi | API authority, native I/O or provider selection |
| `OssmSharp.Device.Primitives` library | IOssmDevice, device values/units, intrinsic validation and semantic errors | Hosting/defaults, application models, transport packages or controller policy |
| `OssmSharp.Device.Runtime` library | Shared provider-neutral DeviceBase, serialized I/O coordination, ownership lease, interruption latch and bounded Stop/cleanup mechanics | Web sessions/grants, HTTP, BLE packages or simulator process ownership |
| `OssmSharp.Device.Stock` library | Stock codecs, profile mapping and Shiny/native BLE integration | Simulator, web policy or UI |
| `OssmSharp.Device.Simulator` library | Loopback simulator connection and native-data mapping | Stock provider, BLE, web policy or process launch/kill |
| `OssmSharp.Defaults` library | Common configurable logging, metrics and tracing through standard .NET hosting | ASP.NET Core, UI, device providers or required Aspire/service discovery |
| `OssmSharp.Web.ServiceDefaults` library | Web-only hosting/health configuration layered on Defaults | Device implementations or mandatory Aspire runtime |
| `OssmSharp.AppHost` executable | Optional local development/test resource orchestration | Production dependency, device connection or motion authority |

There are three web processes, an alternative CLI owner and an independently
started simulator when selected. Libraries are in-process code, not services.
Web.Api owns the same device lifetime as its web application library; a hub or
browser circuit must not create a controller/device instance. The CLI must not
reference Web.Application or another `OssmSharp.Web.*` project.

Device.Runtime is extracted shared behavior, not a new service or general
framework. It prevents both duplicate lifecycle/lock implementations and a
Simulator-to-Stock dependency. The web controller keeps its desired-state slot,
grant deadlines, limits and application guards; the shared runtime provides
the single transport operation gate and immediate Stop/interruption inhibition.
It must not add a second desired-state queue or web arming policy to the CLI.

Runtime's owner session is the sole interruption latch/revision owner, separate
from DeviceBase's protocol publication. Web.Application exposes that state in
its projection; it must not maintain an independently clearable lock flag.
Admin acknowledgement validates role and application authority revision, then
delegates to Runtime with the observed interruption revision. Cli invokes the
same mechanical acknowledgement directly. Application authority revision and
interruption revision have different scopes; neither substitutes for the other.
A new interruption racing acknowledgement prevents it from clearing.

Web.Client is the renamed existing web transport client, not the rejected
API-to-device-service hop. Web.Contracts splits the former mixed Models project:
device data belongs in Primitives; application-private execution types and
operation interfaces belong in Web.Application. Web.Common is not an API
dependency. External package namespaces remain unchanged.

### Calls, state and lifetime

The guest browser communicates only with GuestWeb, and the operator browser
with AdminWeb. Each web host calls Web.Api through its server-side Web.Client.
Browser-to-Blazor circuits and web-host-to-Web.Api SignalR connections are distinct
boundaries. No service key is sent to a browser.

For an operation, Web.Api authenticates the host and permitted role, validates the
wire shape, and supplies its authoritative connection identity to Web.Application.
The library validates application authority, mutates the single serialized
session slot, and supplies trusted execution guards to its controller. The controller
rechecks guards at execution and owns the single device writer. Replies report
acceptance, not physical completion.

For feedback, device observations flow through the controller to Web.Application's
immutable, role-scoped projection. Web.Api delivers that projection through the
ordered push path; Web.Client updates UI state without inventing authority.
Only Web.Api owns SignalR connection objects and delivery subscriptions. The library
must accept connection-loss notifications as ordinary application operations
and expose revocation to Web.Api without depending on transport types. Revocation
must become effective before transport closure and must not await an outbound
notification or guest acknowledgement.

The existing push dispatcher and wire-size checks belong in Web.Api. Web.Application
must determine which application transitions invalidate or supersede earlier
authority; delivery must consume that decision, not derive a second lifecycle
policy from presentation strings. Web.Api owns connection bookkeeping and how those
decisions are delivered. This internal boundary requires no new wire fields.

One guest admission must remain bound to one authoritative Web.Api connection.
GuestWeb must not share that binding between unrelated browser circuits. Browser
loss must stop its sending loop and notify the application when detected;
server timers must not keep its motion alive while its circuit is absent.

The library must own application deadlines and state transitions; Web.Api must own
delivery/backpressure and the host lifetime. Slow delivery must remain bounded
and must not block Stop, expiry or rotation. Overflow must fail closed rather
than grow an unbounded replay queue. Exact queue implementation stays local to
the existing delivery boundary.

Components must use constructor injection and owner-provided
`IServiceCollection` registrations. Web.Api and Cli select their concrete adapter
at their composition roots; application logic consumes only device contracts.
The host owns asynchronous disposal. Request cancellation must propagate where
applicable, but accepted work and unsettled device I/O remain owned until their
outcome is known. A timeout or disconnect is not proof of rollback or settlement.

Arming and applying limits must use core operations that atomically check and
commit execution-side preconditions, not a snapshot read followed by a separate
write. Web.Application must serialize the associated admission, policy and grant
transition around that outcome. Rejection must leave policy, grants and intent
unchanged. Lock acquisition must remain one-way from application authority into
the core; core code must not reacquire application authority while holding a
core lock. Publication and transport callbacks run outside those critical
sections. Splitting assemblies must not introduce a check-then-act gap or a
second copy of pending device state.

### Reusable core boundary

Device contracts, providers and shared execution mechanics must remain usable
without a web host, guest session, CLI executable or orchestrator. In web use,
the application supplies trusted authorization and rechecks it at execution,
including after awaits and immediately before writes. The CLI uses explicit
local commands and the shared device mechanics, never the full web controller.

| Unit | Permitted dependencies and responsibility |
| --- | --- |
| Device.Primitives | IOssmDevice, DeviceState, MotionSettings, device enums and intrinsic validation; no hosting or Defaults |
| Device.Runtime | Primitives and standard .NET hosting/logging/options/time APIs; shared base, one I/O gate, ownership and interruption/Stop mechanics |
| Device.Stock | Primitives and Runtime plus the selected native BLE dependency; private firmware wire types and connection ownership |
| Device.Simulator | Primitives and Runtime plus existing native protocol; no Stock/BLE reference |
| Web.Contracts | Primitives only where a device value is part of a DTO; pure application data |
| Web.Application | Primitives, Runtime and Web.Contracts; controller and session policy; never concrete providers |

Stock and simulator adapters must satisfy the same observable contract, with
shared behavior in Device.Runtime. Neither provider may depend on the other;
ordinary consumers must not branch on simulation. Executable composition may
select the implementation; that dependency must not reach the web controller or
application library. Device integration tests must work without application
hosts. The [adapter design](device-control/adapters.md#composition-and-ownership)
owns the implementation selection details.

Framework hosting, logging, options and timing libraries may support core
lifetime management. ASP.NET Core, SignalR, Blazor, Aspire and UI packages must
remain outside device libraries and web application business logic.

### CLI command and lifetime contract

The CLI is a persistent software remote, not a one-shot sender or background
daemon. Startup options select an available provider and validated connection
settings. After exclusive ownership and connection/readiness checks, the prompt
accepts `help [command]`, `status`, `set <speed> <stroke> <depth> <pattern>`,
`stop`, `acknowledge` and `quit`. Reuse System.CommandLine for command parsing;
do not evaluate shell code. Invalid commands report an error and return to the
prompt without writing, reconnecting or changing the last accepted intent.

Speed, stroke and depth are integer percentages; pattern uses the defined
device identifiers/names and must be supported by the selected provider. The
three-input convenience mapping fixes sensation to 50 and buffer to 0; position
is report-only. Reject invalid tuples and unsupported patterns without
clamping or substituting Simple Stroke. Provider discovery is not physical
qualification; the hardware release gate remains separate. There are no guest
grants, session credentials, Allow control steps or web-policy speed caps in the
CLI. Device validity, Stop precedence and interruption checks still apply.

One prompt owns one device and lock. `stop` writes zero and retains the prompt
and connection. Motion interrupted by an external change stays inhibited until
`acknowledge` succeeds against the current interruption revision and stopped
baseline; acknowledgement leaves paused. Only a new explicit `set` can move.
Help, status, Stop and quit remain available while interrupted. No automatic
motion retries or replay occur on reconnect. Loss reports the failure and
requires explicit restart of the CLI; it does not silently connect to another
device or simulator.

Quit, EOF, Ctrl+C, SIGTERM and catchable terminal-close/hangup events share one exit path:
reject new motion, discard pending intent, wait for already-issued I/O to settle,
then prioritize zero before disconnecting and releasing ownership last. Give
cleanup its own validated finite budget (default five seconds), not the already
cancelled input token. Retry only zero-speed Stop after the preceding attempt
has actually settled and within the same budget; never issue a concurrent write
or retry motion. Expiry reports unconfirmed Stop and a nonzero exit; do not
claim success because disposal or cancellation completed. An OS kill, crash or
power loss can prevent cleanup. A completed zero write is not physical-stop
confirmation. Register the appropriate macOS SIGINT/SIGTERM/SIGQUIT/SIGHUP and
Windows console-close/shutdown handlers; do not rely on asynchronous work in
ProcessExit. Platform termination may shorten the available cleanup time, so
the five-second budget is a ceiling, not an OS guarantee. Signal handlers request
the one owner shutdown path rather than writing the device concurrently.

Web.Api uses this same Runtime shutdown path from its hosted owner, after
revoking web authority and before adapter teardown. Configure its normal host
shutdown allowance above the cleanup budget (default ten seconds for a
five-second device budget). Test ordered Stop, disconnect and lock release on
both owners, including early host cancellation and shortened OS deadlines.

Normal quit/EOF returns 0 after successful command cleanup, startup/connection
or unconfirmed-shutdown failure returns 1, startup usage errors return 2, and
Ctrl+C returns 130 unless cleanup failed (1). Interactive usage errors do not
exit. Redirected stdin uses the same line-command parser without interactive
prompts; EOF still stops before exit. It cannot supply missing startup options
through an interactive selection. `--help`/`--version` need no device. Prompts/diagnostics go to stderr, command
results/help to stdout. Use standard ILogger diagnostics, with no independent
logging system or web dependency.

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
| PST-02 | Defaults owns common host-neutral logging/telemetry; Web.ServiceDefaults adds web-only hosting. Device libraries depend on neither. Web.Application owns capabilities. |
| PST-03 | Models are pure data. Web clients/orchestration own no device. Cli is the explicitly approved alternative direct device owner, excluded concurrently by the shared machine lock. |
| PST-04 | Device abstractions and implementations remain independently buildable/consumable with no controller, application-model, service or UI dependency. |

DEP-01, SESSION-01/02 and OUI/GUI IDs are defined in their sections below.

## Deployment and trust

### DEP-01: Local one-box operation

All three web processes, browser and device adapter must be able to run
on one supported computer without another server, proxy, tunnel, public DNS,
cloud identity or internet access after installation. Runtime assets must be
local. Local and remote guests must use the same policy and execution path.
Cli must run with all web executables absent; Web.Api must run with both UI
hosts absent. Neither deployment requires AppHost. One device user is the admin;
the remote guest is the only separate application role. API/device lifetime
is intentionally shared; preserving a device connection across API deployment
is outside the MVP, not an independence claim.

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

Use one optional AppHost with named `Simulated` and `Actual` launch/debug
configurations, defaulting to Simulated. It starts the three web executables;
Simulated also starts the native simulator as an independent resource and
supplies its loopback endpoint. Actual selects the Stock build/profile and
requires neither simulator assets nor Rust. AppHost never owns BLE or calls
device operations. For CLI-only development, start the simulator independently
and the CLI directly; do not launch Web.Api as another device owner. A runtime
launch profile does not replace provider selection before restore/publish.

Use the MSBuild property `DeviceProvider=Simulator|Stock`, default Simulator,
consistently across a solution restore/build. Establish profile-specific
artifacts and intermediate/restore directories before SDK evaluation; for
example, separate `artifacts/Simulator` and `artifacts/Stock` trees retain their
own project.assets.json and generated files. Never use no-restore outputs from
another profile. The Actual run/debug configuration invokes a Stock build of
AppHost and its provider-consuming projects; runtime options must match that
build. CI builds/tests both profiles on Windows and macOS where native tooling
is required; simulator acceptance does not wait for physical qualification.

Outside Aspire, start executables using standard .NET hosting and explicit
configuration. No custom orchestrator is required. Device.Simulator only
connects; the process starter, including AppHost in development, owns simulator
shutdown. A client disconnect must leave the simulator process alive at zero.

AdminWeb and GuestWeb must have distinct 256-bit service keys. AppHost may
generate/inject these for development; without it, the admin supplies matching
keys through environment/protected host configuration. Missing keys fail
startup; do not generate independently mismatching keys or require a dashboard.
Web.Api authenticates hosts, not humans; Web.Application separately validates
guest capabilities. Keys must never appear in process arguments, routine logs
or browsers. The CLI requires no service keys or application authentication.

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

Web.Api or Cli must exclusively own its device lifetime. Both must acquire the
same machine-wide ownership lease before discovery, connection or writes and
retain it through managed shutdown. Put the lease in Device.Runtime, not a web
assembly or per-user CLI daemon. Use a crash-released exclusive OS file lock in
a provisioned machine-wide runtime directory, with one stable device-owner key,
not a per-user temp path. Keep the file in place after release to avoid unlink/
recreate races. Provision access only to intended local operator identities;
missing permissions fail clearly, without fallback to a weaker lock. This
excludes cooperating software on one computer, not the physical remote or
arbitrary compromised local code. Do not steal a lock on a timeout.

Resolve the lock as `OssmSharp/runtime/device.lock` beneath Windows common
application data (`ProgramData`) or the macOS system-domain
`Library/Application Support` directory, not a user Library or temporary
directory. Installation/setup documentation must provision the directory and
grant the intended local admin identity access; neither running owner escalates
privileges automatically. There is no production option/environment override
for the lock path. Internal test helpers may inject isolated directories for
unit/helper-process tests; tests of the real CLI/API use the production path on
dedicated test machines, never steal or terminate an operator's existing owner.

Unexpected observed parameter changes must latch an interruption, invalidate
pending motion/guest authority and request serialized Stop. Never restore the
prior tuple to counteract physical input. AdminWeb acknowledgement or the CLI's
local `acknowledge` command clears only into paused state under DC-09. The same
human is the device user and admin; no extra identity/account is introduced.
Heuristics cannot detect every remote action or establish physical stopping.
Physical-use limits remain in the
[qualification contract](device-control/spec.md#physical-qualification).

### SESSION-02: Accountless access

The application must not require accounts, registration, login, passwords,
personal identities, a user database or an external identity provider. Operator
is a local management role; guest means scoped capability possession, not a
verified identity.

Only Web.Application may create guest capabilities. Session secrets must be
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

After advertised-origin validation, Web.Application must create one high-entropy,
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
slot to pending. Concurrent joins admit exactly one connection. The internal
application Join result contains a private connection-bound admission proof;
the API retains it and the hub reply only acknowledges. No snapshot, grant or
private proof is returned to the browser. Every guest operation must validate
proof, session, generation and the
authoritative Web.Api connection binding, whether the proof is carried explicitly
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

Web.Application must approve the exact admitted guest before arming. **Allow
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
The endpoint belongs to Web.Api; the two web hosts use Web.Client. Do not add
an API-to-ControlService hop, REST control or browser-to-Web.Api access. The
CLI uses device libraries, not this service transport. The simulator connection
is a separate device transport, not an application authority endpoint.

The requester waived backwards compatibility for this WIP. Update repository
consumers together; retain no forwarding types, old command daemon, legacy
stdio mode or parallel compatibility stack. Preserve the current web method
names, bounded payloads and errors because no accepted behavior requires
changing them, not to maintain an old product version. Keep existing safety
regressions and distinguish behavior corrections from mechanical renames.

| Caller | Allowed operation family | Result path |
| --- | --- | --- |
| AdminWeb service role | Subscribe operator state/logs, read invitation, Stop, Cycle/operator End, approve/Allow control, arm, apply limits, recover/unlock | Mutations acknowledge acceptance/rejection; invitation, state and safe logs arrive through ordered operator push |
| GuestWeb service role before admission | Stage subscription and Join using invitation proof | Hub reply acknowledges only; the application admission proof stays private in the connection registry |
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

Closed schemas retain explicit units, interpretation and errors. Update current
repository participants together when an accepted change alters that contract;
older WIP clients need not coexist. Unknown or mismatched payloads still fail
closed without weakening Stop, freshness or authorization. Do not add a
compatibility framework or relax validation to conceal version mismatches.

### Guest input and grant freshness

Version-2 input must contain `v = 2`, control epoch, policy version, grant ID,
a strictly increasing positive safe-integer `seq`, `run = true`, and a complete
valid tuple. The server resolves the private session/connection proof and grant
record from the authenticated Web.Api binding. Grant sequence, issuance and
expiry are authoritative server-side metadata, not required additional input
fields. Web.Api's connection identity is authoritative. Version-1 hold inputs, nonincreasing sequences,
replays and expired/superseded/cross-session/epoch grants must reject without
writes or lease renewal.

Web.Application issues an initial grant on explicit arming and one every 250 ms
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
20-capacity bucket in the earlier extraction. The current rolling behavior is
baseline behavior to preserve; this migration must not reintroduce that bucket.

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

Application notices stay at the top right outside normal layout flow, consistent
with the existing repository UI contract. Framework-managed connection notices
remain outside interactive-route DOM. Connection failures
take precedence over other notices.

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
| OUI-03 | The shared site label is 1.4rem. SIMULATOR, when present, aligns at the page header's top-right with the label/widgets, not in Controls. The Controls guest-session indicator is right-aligned and shows **Controls disabled** rather than **Guest connected** when an approved guest needs Allow control. Pending device I/O adds a preceding large recording-red dot with accessible text/tooltip **Waiting for the device to settle.**, not a normal-flow status message. |
| OUI-04 | **Session limits** spans the grid, with dirty status top-right. All operator/guest card headings render at 1.1rem without changing semantic levels; the limits landmark remains h2. |
| OUI-05 | Speed/depth share one right-aligned **Apply limits** action and atomic policy operation, without selected/applied text rows. A primary/selected-colour phantom dot marks applied track position while drafts differ and disappears on equality after successful application. Refresh preserves unsent edits. |
| OUI-06 | Both interfaces share the header/content template and top-right notification behavior. An actionable pending guest shows persistent warning/gold **A guest is waiting. Click Allow control to enable control.** Allow control is green when actionable and grayscale when inactive, without an activation glow. It is enabled whenever the operator can safely allow control, including after guest connection changes. Readiness and safety checks still govern availability. |

### Guest presentation requirements

| ID | Requirement |
| --- | --- |
| GUI-01 | The shared main region has no initial visible focus outline; keyboard focus remains visible when used. The status card is **Live Status**, with state and applicable SIMULATOR badges in its right-aligned heading group and no numeric position, velocity or source-caption rows. |
| GUI-02 | Non-wrapping **Auto send** immediately precedes green **Send**; Auto send, Send and Stop are right-aligned together. Paused uses warning/gold without duplicate operator-disabled guidance. Terminated sessions say **Session ended. Open the invite link again to rejoin.** |
| GUI-03 | Invitation-required, interactive guest and operator routes share the page layout and top-right notices outside layout flow. The connection-loss danger alert remains above live feedback, with connection failures taking precedence. |

## Decisions and alternatives

| Decision | Strongest practical alternative | Deciding trade-off |
| --- | --- | --- |
| Web.Api owns the device for the MVP | Separate device executable and SignalR client hop | Accepted for one computer/device/admin/guest: fewer lifecycles and credentials. API restart also disrupts device ownership. Revisit only for a concrete remote-host or independent-restart requirement. |
| CLI is a direct interactive remote | Web-controller/service client or send-and-exit tool | No web policy/dependencies; a persistent prompt retains exclusive ownership and can strongly attempt Stop before exit. |
| Separate Primitives, Runtime and providers | Keep the shared base in Stock or put web policy in Primitives | Runtime serves two real providers/consumers and isolates BLE; web authority/controller stays in Web.Application. |
| DI plus selected-provider build profiles | Ship every provider and select at runtime | Runtime options do not prune package dependencies. Stock and Simulator artifacts contain only their selected provider. |
| Independent loopback simulator | Adapter-owned child, native ABI or network service | Retain Rust motion and bounded JSONL, move it to loopback TCP, keep the process alive across client exit. No additional engine or remote-service auth is required in the trusted-local MVP. |
| Defaults plus Web.ServiceDefaults | Give the CLI ASP.NET/service-discovery dependencies | Share logging configuration without pulling web hosting into the direct remote. |

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

The local admin observes standard logs and web health/UI state without Aspire.
An optional development dashboard is another consumer, not an operational
prerequisite. Liveness must not imply device
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
must have reviewed access/retention and exclude secrets before use; this design
does not claim an existing retention policy. The split adds no new licensing
selection or clearance and does not replace physical qualification obligations.

## Delivery and verification

Implementation is subsequent work. The
[migration plan](../plans/direct-device-migration.md) owns the ordered tasks,
rename inventory, repository-instruction updates and acceptance evidence. The
[earlier extraction plan](../plans/web-control-implementation.md) is historical
evidence only. Record behavior corrections separately from moves. Completion
requires a CLI with no web dependencies and independently started simulator,
plus the API path and configured telemetry working without AppHost.

Restart during cutover discards authority and starts paused/disarmed; it must
not migrate live sessions or resume accepted input. Reverting packaging likewise
requires stopping the managed owner first, not running two writers. No schedule,
performance gain or deployment authorization is implied.

### Implementation and verification

Web.Application tests must exercise application authority without ASP.NET or
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
| Web.Api and Web.Client | Service-role separation; closed schemas and envelope bounds; ordered initial/live push; unchanged projections do not republish; expired authority closes the guest connection; loss and cancellation do not revive motion |
| Local host | Wrong Host and missing/foreign mutation Origin reject; listener overrides cannot expose AdminWeb; a second authority host cannot acquire device ownership; one-box startup works without external networking |
| Controller/device | DC safeguards tested by reference: one writer, execution-time guards, Stop priority, unconfirmed I/O and paused recovery; adapter checks remain host-independent |
| Cli and shared runtime | Persistent prompt without any web executable; complete tuple/pattern validation; CLI/API cross-process exclusion; interruption acknowledgment into paused; strong bounded Stop on all handled exits; simulator remains alive at zero |
| Packaging/defaults | Simulator publish has no BLE/native platform dependency; Stock has no simulator/Rust dependency; CLI has no ASP.NET/UI/Aspire/application-policy dependency; logs/export work without Aspire and startup works without a collector |
| UI/components | OUI/GUI contracts, responsive keyboard order and focus, reduced motion, send/draft separation, freshness labels, clipboard fallback, connection-loss precedence and restart behavior |

Use the lowest test level that proves each behavior, with focused browser checks
for high-value cross-boundary flows. Tests must not predominantly validate Aspire
or other dependencies. Simulator fault injection stays operator/test-only.
Software conformance must never be presented as hardware safety.

## Resolved implementation questions

| Question | Decision |
| --- | --- |
| Must another device service exist? | No. Web.Api owns the web device lifetime; the CLI is an alternative direct owner. |
| Where does controller code go? | Shared mechanics move to Device.Runtime; the full controller and web authority live in Web.Application. Neither is folded into Primitives. |
| Which grant metadata crosses the wire? | Retain grant ID/epoch/policy plus input sequence; resolve grant sequence, issuance and monotonic expiry from server records. Status/grant publication cannot extend accepted motion. No extra public fields are needed. |
| Which Join boundary returns the proof? | The application returns the private proof to the API registry; the hub acknowledges only. The proof never becomes browser data merely to satisfy wording. |
| Does an invitation survive API restart? | No. It is non-expiring within the process lifetime, reused until explicit rotation, and replaced after restart. All authority is memory-only. |
| Simulator connection and lifetime? | Bounded JSONL over IPv4 loopback TCP, one controller connection, explicit configured endpoint, independently started/stopped process. Connection loss zeros the simulator and clears pending input; it does not exit. |
| CLI lifetime and interruption? | Persistent REPL; explicit local acknowledgement; Stop retains the prompt; handled exit awaits bounded cleanup without web policy. |
| How are providers selected? | Stock/Simulator build profiles prune references before restore; typed options configure the one available provider. Missing/unavailable provider is a startup error. |
| What does the rename include? | Repository identity, solution/workspace, project/folder/assembly/package names, namespaces, tests, friend assemblies and owned documentation links. Do not rename stock protocol fields/UUIDs, external packages or the ossm-rs engine. |
| What compatibility is required? | None for this WIP. Update consumers/configuration together, remove superseded paths, and retain no aliases or legacy daemon/stdio interface. |
| Can implementation start? | No. This change publishes design and a migration plan only. Repository instructions are updated in the first separately authorized implementation task. |

### Resolved direction and review

- **SignalR, not REST:** the requester changed the initial REST brief to retain
  SignalR. This keeps the existing live connection and authority semantics.
- **MVP ownership (25 September 2026):** the requester accepted Web.Api as the
  web device owner and a direct standalone CLI. The previous separate-service
  requirement is superseded deliberately, not considered already satisfied.
- **Rename (25 September 2026):** the requester replaced OSSMDotNet repository,
  project and namespace identities with OssmSharp.
- **Compatibility (25 September 2026):** the requester waived backwards
  compatibility for this WIP migration. No compatibility layer is planned.
- **Public .NET moves (22 September 2026):** the requester selected coordinated
  repository-consumer updates without source/binary compatibility shims for the
  earlier extraction. The broader 25 September WIP waiver governs this migration.
- **Rolling limit (22 September 2026):** the requester selected 10 authenticated
  run-input attempts in any rolling monotonic second; the prior plan records
  its implementation. Preserve that baseline and its tests; a 20-capacity bucket
  is not equivalent.

No architecture question remains deferred to an implementer. Platform BLE
support, feedback adequacy and physical stopping remain evidence gates in the
device contract, not permission to weaken it or claim that review proved it.
