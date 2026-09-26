# OssmSharp direct-device migration plan

Date: 25 September 2026

Status: implementation in progress. M0-M1 are complete for the local Windows
instruction and project graph; M2-M3 are partial, and M4-M8 have not started
their acceptance work. The
[accepted design](../specs/web-control-design.md) owns decisions and contracts;
this file owns the ordered migration work and acceptance evidence. The
[previous extraction plan](web-control-implementation.md) retains historical
results, which are not passes for this migration.

## Scope and decisions

One computer, one device, one local admin/device user and one guest. Web.Api
owns the selected device when the web application runs. Cli is a persistent
interactive alternative owner, with no web controller, sessions, authentication
or service client. A machine-wide lock excludes concurrent owners; detected
external changes latch an interruption requiring explicit local acknowledgement
into paused state. Neither acknowledgement nor reconnect resumes motion.

The simulator is independently started and uses bounded JSONL over loopback
TCP. Its adapter does not own the process. Stock and Simulator are alternative
build/publish profiles, selected before restore, and DI configures the one
available provider. Defaults is host-neutral; Web.ServiceDefaults adds web
hosting. AppHost is development/test convenience only. API/device lifetimes are
intentionally coupled; the UI hosts and CLI do not need to be deployed together.

The requester waived backwards compatibility for this WIP. Coordinate consumer
moves and remove old identities and interfaces without forwarding types,
namespace aliases, command shims, a daemon compatibility layer or legacy stdio
mode. Preserve valid behavioral regressions, not obsolete project layouts.
Keep the web SignalR route, methods and payload meanings unless an accepted
behavior correction requires a change; do not invent a second protocol version
solely for the rename.

## Project and identity map

All target folders, project filenames, assembly/root namespaces and published
owned package identities use the same `OssmSharp.*` names. External package
names, BLE UUIDs, firmware fields, `ossm-rs` and `ossm-sim-bridge` are not owned
OSSMDotNet identities and remain unchanged.

| Current owner | Target owner and action |
| --- | --- |
| Repository identity OSSMDotNet | OssmSharp. The local checkout directory already has this name; verify the hosted repository name and update owned remote/badge/package/release references only in an authorized publication step. |
| OSSMDotNet.slnx / OSSMDotNet.code-workspace | OssmSharp.slnx / OssmSharp.code-workspace; update project entries, relative paths, launch tasks and developer documentation. |
| OSSMDotNet.Abstractions | OssmSharp.Device.Primitives: IOssmDevice, values, enums and semantic device exceptions. |
| Shared DeviceBase, neutral options, lifecycle/Stop machinery and device lease | OssmSharp.Device.Runtime, extracted from OSSMDotNet, ControlCore and API/CLI ownership code. No web policy, BLE, process launcher or Defaults dependency. |
| OSSMDotNet stock codecs/provider | OssmSharp.Device.Stock; move Shiny/native integration out of the CLI and isolate platform targets here and in selected hosts. |
| OSSMDotNet.Simulator | OssmSharp.Device.Simulator; depend on Primitives/Runtime, not Stock; replace child-process transport. |
| OSSMDotNet.ControlCore + OSSMDotNet.ControlService | OssmSharp.Web.Application: retain full web controller, authority, operations and activity policy as transport-neutral classes. Extract only genuinely shared mechanics to Runtime; do not expose web policy to Cli. |
| OSSMDotNet.Models | Split device values to Primitives, public web DTOs to OssmSharp.Web.Contracts, execution/private policy types to Web.Application. |
| OSSMDotNet.ControlClient | OssmSharp.Web.Client, for AdminWeb/GuestWeb to Web.Api only. No CLI or API-to-device-service consumer. |
| OSSMDotNet.ApiService | OssmSharp.Web.Api executable, composing Web.Application plus one provider. |
| OSSMDotNet.AdminWeb / GuestWeb | OssmSharp.Web.AdminWeb / OssmSharp.Web.GuestWeb. |
| OSSMDotNet.WebCommon / SharedUi project | OssmSharp.Web.Common; UI only, not an API dependency. |
| OSSMDotNet.DeviceCli | OssmSharp.Cli executable; replace per-user diagnostic daemon/IPC with the foreground REPL. |
| OSSMDotNet.ServiceDefaults | Split host-neutral registration to OssmSharp.Defaults; retain web-specific registration in OssmSharp.Web.ServiceDefaults. |
| OSSMDotNet.AppHost | OssmSharp.AppHost; one executable with Simulated/Actual development configurations. |
| tests/OSSMDotNet.* and short test assemblies | Move to their owning target assembly plus .Tests or .IntegrationTests; update friend assemblies, solution entries, filters and shared fixtures together. |

Current source links in the design are baseline evidence and must be updated at
cutover, not treated as target filenames. Preserve historical evidence documents
as history. Class names such as IOssmDevice and stock command/profile names do
not need cosmetic changes. Keep `OSSM:*` configuration keys that describe the
device domain; replace the obsolete simulator executable-path setting with the
endpoint setting, without a compatibility alias.

## Baseline to preserve

- The current API hosts ControlService as a library and selects the simulator.
  The four-process target recently added to AGENTS.md is not implemented and is
  now superseded. Do not build that obsolete service split first.
- At the migration baseline, DeviceCli used device libraries and a private
  per-user simulator host. The renamed Cli still uses that helper process,
  not an interactive REPL.
- At the migration baseline, SimulatedDevice started and owned a stdio child,
  while DeviceBase lived in the stock project and consumed stock-named options.
  M1 extracted DeviceBase and neutralized its options; M3 subsequently changed
  the native bridge and managed adapter to loopback TCP. Cli has not followed.
- Stock motion remains deferred. The existing conditional macOS/Shiny CLI path
  only identifies a device; it does not prove a stock motion implementation or
  physical qualification.
- The old plan's passing test counts are historical. Establish a new focused
  baseline and record actual outcomes during implementation. Do not assume
  renamed tests or a successful solution build prove the new architecture.

## Ordered work

M0-M1 have local Windows evidence; M2-M3 are **in progress**; M4-M8 have not
started their acceptance work. Each task includes its focused validation;
behavior changes must first have a regression failing for the intended reason.
Use existing xUnit v3/MTP v2 projects, FastMoq where suitable, AwesomeAssertions,
TimeProvider fakes and shared deferred-I/O fixtures. No new test framework or
dependencies are authorized by this plan.

### M0. Align repository instructions before code

Depends on: separate implementation authorization.

Status: complete on 25 September 2026. Root instructions now describe the
direct-owner topology and link to this plan's acceptance matrix. Focused
instruction content/link and diff-format checks passed; an independent
read-only review found no actionable contradictions. No runtime checks ran.

Owner/scope: root AGENTS.md and any applicable repository-local instructions;
do not change workbench-wide policy as part of this application migration.

Replace the obsolete rules with these exact architectural requirements:

- Use OssmSharp namespaces and the target project map above; do not enforce old
  ApiService/ControlService names or a blanket executable suffix convention
  against the approved Web.Api/Cli names.
- Cli is a direct REPL consumer of Primitives/Runtime and the selected provider;
  forbid dependencies on any OssmSharp.Web project, ASP.NET, UI or Aspire.
- Web.Api owns one application/controller/device lifetime. Remove the required
  standalone ControlService executable, ControlClient CLI route and the ban on
  API composition referencing providers. Only UI hosts use Web.Client.
- Keep AdminWeb, GuestWeb and Web.Api separate processes. Require CLI-only and
  API-without-UI deployments; do not claim API/device restart independence.
- Put machine-wide exclusive ownership and mechanical interruption/Stop rules
  in Runtime. Local admin equals device user. Physical override acknowledgment
  leaves paused, never restores the old tuple, and is a CLI command in REPL mode.
- The simulator starter owns its process. Clients may connect/disconnect but
  must not launch/kill it. AppHost is optional and never owns BLE.
- Defaults must remain host-neutral, Web.ServiceDefaults web-specific, and
  configured logging/telemetry must work without Aspire or a running collector.
- Preserve existing Host/Origin protection, role separation, guest capability
  checks, UI/CSP/package rules, requirement IDs and physical qualification gates.
- Replace the obsolete service-split verification paragraph with the acceptance
  matrix below. Correct the existing solution/test command paths at rename time.

Acceptance: no instruction mandates the rejected topology or contradicts the
accepted CLI/DI/lifetime model. Verify names, local links and relevant instruction
frontmatter; do not mark runtime checks passed from this editorial task.

### M1. Characterize and establish the target dependency graph

Status: complete for the local project graph on 26 September 2026. The slices extracted
`DeviceBase`, its shared logging and provider-neutral `DeviceRuntimeOptions`
into `OssmSharp.Device.Runtime`, device contracts and values into
`OssmSharp.Device.Primitives`, and stock codecs/provider into
`OssmSharp.Device.Stock`. Simulator consumes only Primitives and Runtime;
Web.Application does not reference Stock. Primitives contract tests passed 14,
Runtime tests 40, Stock tests 151, CLI Identify tests 11, CLI simulator-host
tests 17 and native Simulator integration tests 72. Focused Runtime, Simulator,
then-ControlService and CLI builds passed on Windows. All 30 local solution
projects resolve without cycles. CLI still references
both Stock and Simulator; M6 must select one provider before restore. Stock's
macOS-only identity reader was not compiled or tested on Windows.

Web.Contracts now owns 25 public DTOs, including the operator projection and
its transitive value types, without changing record bodies or wire shapes.
Web.Application owns the four former Models-private authority and execution
types; Web.Client has no application-policy dependency. Protocol and hub-payload tests passed 13,
real SignalR hub contract tests passed 53 and client lifecycle tests passed 2.
After the operator move, focused Web.Client tests passed 57, Core limits tests
23, authority and invitation tests 54, payload field-order test 1 and real hub
proof/ordering tests 4. Contracts has no Models dependency; this is not full
M1 or API protocol acceptance.

The renamed `OssmSharp.Web.Client` is used by AdminWeb and GuestWeb, not by
Web.Api or OssmSharp.Cli. Its 57 tests passed under native MTP; both UI hosts and
their test projects built on Windows. This verifies the project move, not
independent UI/API startup or the full web control journey.

`OssmSharp.Web.Common` now owns the shared Razor components and static assets.
The guest HTTP asset check first failed with 404 for the new path, then passed
after both hosts updated their CSS and JS URLs. MotionMonitor tests passed 6;
AdminWeb and its test project built. Widget contents and CSP hashes did not
change. Full browser control and operator asset loading remain to verify.

At the M1 identity checkpoint, `OssmSharp.Device.Simulator` and its integration
test assembly had the target identity but still used adapter-owned stdio. The
native integration cases passed 72, API composition 1, CLI simulator-host
unit cases 17 and process cases 4, and activity cases 11 on .NET SDK 10.0.401
on Windows. The affected API/service integration projects built. The provider
still depends only on Primitives and Runtime, not Stock. Independent simulator
startup, lifetime and cross-platform evidence remain unverified.

`OssmSharp.Defaults` now owns host-neutral file logging, HTTP/runtime telemetry
and optional OTLP registration; `OssmSharp.Web.ServiceDefaults` adds web-only
instrumentation, health, discovery and hosting safeguards. Its registration,
HTTP-boundary and token tests passed 29, and all three web hosts built on
Windows. The neutral resolved graph contains no ASP.NET, SignalR, Blazor,
Aspire or service-discovery dependency. CLI use, configured export and startup
without a collector or AppHost remain M6 checks; capability-token ownership
still moves with Web.Application.

Neither UI host references application policy, directly or
transitively; each resolves through seven projects without that edge. The
focused guest application tests passed 9 and operator home tests passed 13
on Windows. UI/browser conformance is not established by this dependency check.

`OssmSharp.Web.Application` now contains the former ControlCore, ControlService
and private Models responsibilities in one transport-neutral library. The moved
unit tests passed 233 and simulator integration test passed 1. Focused API
checks passed 14, 8, 55 and 10, and the API builds against the new library.
The evaluated 30-project graph has no cycles or missing references. The
application library has no provider, UI host, client or ASP.NET dependency;
Runtime has no application dependency. This preserves existing behavior only:
the M2 ownership and interruption mechanics, M3 transport and M5 protocol
conformance remain outstanding.

`OssmSharp.Web.Api` now names the existing device-owning backend and its unit
and integration test assemblies. The API and AppHost built on Windows with
.NET SDK 10.0.401. Focused unit checks passed 41 and 22, real URL-override
process cases passed 2, and cross-process shutdown ownership passed 1.
Before the rename, both URL-override cases failed specifically because the
new executable was absent. A positive certificate-path test exposed a Unix-
only fixture on Windows; it now passes with a platform-qualified test path,
without changing production validation. This does not establish API-without-
UI startup, independent simulator lifetime or M5 protocol conformance.

The two UI hosts and their unit test projects now use the target Web.AdminWeb
and Web.GuestWeb identities. Guest application checks passed 9, Join page 17,
operator Home page 13 and notice checks 4; AppHost builds against both renamed
hosts. Both UI dependency graphs exclude application policy and device
providers. Live browser workflows and UI-independent API operation remain
unverified.

`OssmSharp.Cli` and its unit/integration test assemblies now have their target
identity. Its process test first failed for the missing renamed DLL, then
passed after the move; CLI unit tests passed 40 and integration tests passed 8
on Windows. The CLI references no `OssmSharp.Web.*` project, but retains the
old daemon/IPC behavior, ASP.NET framework reference and both device providers.
The M4 interactive and M6 selected-provider checks have not run; macOS BLE
qualification remains separate.

`OssmSharp.AppHost`, `OssmSharp.slnx` and `OssmSharp.code-workspace` now name the
local orchestration and developer entry points. The AppHost build passed with
.NET SDK 10.0.401 and Aspire AppHost SDK 13.5.4; the solution lists 30 existing
project paths. The root instructions and current test commands resolve. The
default `OSSMDotNet/logs` directory remains during the migration so existing
operators do not silently lose log continuity; M8 owns its coordinated rename.
The private CLI IPC name remains only until M4 removes that path. No full-suite,
macOS or runtime acceptance follows from this graph migration.

Depends on: M0.

Owner/scope: solution, Directory.Build.props, Directory.Packages.props, source
project files, namespaces, registrations and test ownership.

Inventory all tracked OSSMDotNet identities and references, including assembly
names, package metadata, InternalsVisibleTo, Razor imports/static assets, XAML/
native metadata where present, launch configuration, CI filters and links.
Move projects/types according to the map, splitting mixed Models and defaults
by actual responsibility. Convert stock-specific base options to neutral
runtime options. Retire ControlCore/ControlService identities after moving their
surviving responsibilities into Web.Application and Runtime.

Do not use wholesale text replacement on protocol constants, external package
IDs or historical evidence. Do not drop tests attached to a retiring assembly;
move their behavioral subjects to the new owner. Preserve current user changes,
including any already removed legacy CLI files, instead of resurrecting them.

Acceptance: the graph has no cycles; Primitives has no hosting/defaults/provider
dependency; Runtime has no web/provider dependency; Simulator does not reference
Stock; Web.Api does not reference UI components. Existing behavior-scoped tests
pass under new names. Verify direct and transitive references, not just source
imports. Repository-rename publication is not performed here.

### M2. Shared execution, ownership and interruption mechanics

Status: in progress on 26 September 2026; **not accepted**. Runtime now owns a
fixed machine-wide lock-file path, and Web.Api and the current Cli simulator
host acquire that lock before device access. Isolated tests cover contention,
release, missing directory and access denial. Web.Api retains ownership on a
reported cleanup failure by terminating the owner; DeviceBase shares one
disposal outcome and bounds Stop, connection settlement, issued-write waiting
and core cleanup within five seconds. Focused DeviceBase/lock tests passed
48/48, two simulator disposal cases passed, and the API built. These checks
do not prove the full M2 safety contract.

The production runtime directory was absent on the Windows development
machine and was not provisioned automatically. Four real CLI/API process
cases were not run against the new lock. Windows/macOS cross-user competition,
owner crash, permission and delayed-cleanup evidence still require a dedicated
provisioned native CI environment. Cli's macOS `identify` path still reaches
BLE discovery without first acquiring the shared lock; macOS must also fail
closed if .NET file locking is disabled. The sole Runtime interruption latch,
stale-revision acknowledgement, shared Stop path and between-write revocation
checks are not implemented. Existing CLI daemon and API disposal evidence
cannot substitute for those requirements.

Depends on: M1.

Owner/scope: DeviceBase and device options, DeviceOwnershipLock, common
operation settlement/Stop coordination and semantic error publication.

Extract the single I/O gate and minimal interruption revision into Runtime.
Preserve no-full-state-cache DeviceBase behavior; each consuming owner retains
the state snapshot it needs. Reject new nonzero operations synchronously on
Stop/interruption/shutdown; discard pending motion and recheck revocation
between actual transport writes. Keep the web controller's desired-state slot
and guards, but do not create a second runtime queue or import grants into Cli.

Runtime owns the only interruption latch and revision. Web.Application projects
that state and adds role/authority-revision checks before Runtime acknowledgement;
it must not keep a separately clearable device lock. Reuse one acknowledged
paused outcome across web and CLI paths.

Use one crash-released exclusive lock-file handle in a machine-wide provisioned
runtime directory. Both executable identities resolve the same stable key,
including different launches/users with permitted access. Do not use per-user
temporary paths, unlink an active lock file, steal ownership after a timeout or
fall back on permission failure. Acquire before discovery/connection; retain
until issued I/O/cleanup is complete or the owning process terminates.

Provision the design's common-application-data/system-Library runtime directory
through documented administrator setup, with no automatic elevation or
production path override. Keep helper-test path injection internal; run real
CLI/API process competition in dedicated native CI environments using the
production location. A pre-existing operator owner blocks that test rather than
being killed or bypassed.

Detected external changes raise the common semantic event and latch inhibition;
Web.Application revokes guest authority, while Cli reports and accepts only
non-motion commands until explicit acknowledgement. Acknowledge verifies the
current revision and stopped baseline; it never resumes. Missing/late feedback
remains unknown/faulted, not a fabricated override or confirmation.

Verification: reuse DeviceOwnershipLockTests, ApiProcessOwnershipTests,
ControlExecutionTests/ExecutionOwnershipTests, CoreRig/FakeBridge and adapter
expectation tests. Add deterministic Stop-vs-issued-write and stale-ack tests;
process tests cover CLI/API competition, failure before connect, crash recovery,
permission failure and retention during delayed cleanup on Windows/macOS.

Acceptance: one writer and shared exclusion in either consumer, no web-policy
dependency in Runtime/Cli, zero-speed priority without concurrent I/O, no replay
after interruption/reconnect, and explicit unknown outcomes on cleanup timeout.

### M3. Independent simulator transport

Status: in progress on 26 September 2026; **not end-to-end runnable or
accepted**. The native bridge now requires an explicit IPv4 loopback TCP
listener, keeps one controller at a time, stops on client loss and stays alive
for a successor. Its stdio and client-shutdown modes were removed. Native
tests passed 47/47, including a delayed-write successor regression. The
managed Simulator adapter connects through a loopback endpoint instead of
starting or killing the process; test-owned bridge fixtures passed 92/92
simulator integration cases. Focused API endpoint/composition cases passed
47/47 after correcting an uninformative options error; AppHost builds with
the simulator as a separate local resource. These are focused Windows checks,
not independent-CLI or full-web acceptance.

On 26 September, the adapter now invalidates sends queued before disconnect
intent and refuses new writes while disconnect is pending or the last validated
state is disconnected. It clears that inhibition only after a successful
reconnect response reports connected. The local-rejection and reconnect
regressions passed, and the focused `SimulatedDeviceTests` file passed 89/89 on
Windows. This does not establish full M3 acceptance.

On 26 September, connection transitions in the simulator fault hooks were
reduced to the validated reply samples; post-await publications were removed
so a delayed helper continuation cannot overwrite a newer transition. The
focused `SimulatedDeviceTests` file passed 89/89, including frozen-feedback,
disconnect and reconnect cases.

Cli still uses the obsolete per-user daemon/executable-path flow, and a
temporary `SimulatedDeviceOptions.ExecutablePath` member remains only to keep
that incomplete consumer compiling. It is not a working fallback for the
TCP-only bridge and must be removed at the M4 cutover. Stop versus an
intentional zero-speed configuration write is indistinguishable after some
external changes under the current device contract. Resolve that contract
question before dependent motion work. AppHost readiness only checks socket
acceptance; unavailable-listener and handshake deadlines, exact 4 KiB CRLF
framing, CLI/API client-exit zero and macOS behavior remain unverified.

Depends on: M1; integrate ownership with M2 before end-to-end acceptance.

Owner/scope: tools/ossm-sim-bridge transport/framing/protocol, SimulatedDevice
connection lifetime, options and native/.NET integration fixtures.

Replace stdio ownership with a listener bound only to an explicit IPv4 loopback
endpoint. Reuse existing bounded JSONL codecs and native engine; use .NET
network streams and Rust standard-library TCP, adding no dependency. Support one
controller connection; reject another without replacing the current owner.
Connection loss clears pending input and sets engine speed to zero, invalidates
that connection's request generation and leaves the process listening. Late
work from a closed connection cannot run for its successor. The adapter never
sends a process-shutdown request, launches a process or terminates one.

Preserve the existing 4 KiB frame bound and eight-request adapter bound. Keep
native command IDs private. Native `stop` preserves current settings; a zero-
speed configuration change uses `write` with zero speed. Keep both paths tested
so Stop cannot restore stale fields and stopped configuration is not discarded.
Remove the legacy stdio mode at coordinated cutover; do not ship both models.

Verification: reuse framing.rs/protocol.rs tests and SimulatedDeviceTests with a
test-owned server process. Cover partial/oversized frames, delayed writes,
malformed replies, independent startup, refusal of a second client, frozen
feedback, injected external changes, disconnect while work is pending and
successive clients. Closing CLI/API must leave the simulator process alive and
at zero. Kill only test-owned processes in teardown.

Acceptance: runtime provider requires an endpoint, not an executable path; no
adapter Process.Start/Kill or child job ownership; no BLE or web dependency.

### M4. Interactive direct-device CLI

Status: not started. M1 renamed the executable and tests, and the partial M2
lock reaches its legacy simulator host; neither change supplies the direct
foreground REPL. Daemon IPC, launcher ownership, the ASP.NET framework
reference and both provider references remain. Current Cli cannot complete
the TCP-only simulator path; do not use M1's old CLI passes as REPL evidence.

Depends on: M2 and M3 for the simulator acceptance path.

Owner/scope: Cli entry point, DeviceCommand/runner, device selection/options and
the existing DeviceCli unit/integration tests.

Replace per-command daemon IPC with the design's foreground prompt. Keep one
connection/lock across help, status, set, Stop and acknowledgement. Reuse pinned
System.CommandLine parsing; commands have three numeric inputs and pattern,
with provider capabilities and model validation enforced. Help/version require
no connection. Invalid input returns to the prompt without sending work.

All handled exits share the five-second-default cleanup budget: stop intake,
invalidate pending work, wait for issued I/O, attempt serialized zero, then
disconnect and release ownership last. Only retry zero after actual settlement
and within the budget. Report unconfirmed cleanup, preserve cancellation exit
semantics and never background a replacement owner. Remove SimulatorHost/IPC
and launcher code only after replacement tests cover their surviving behavior.

Verification: reuse DeviceCommandTests and DeviceCliProcessTests; add real
stdin/stdout REPL sessions against the independently started simulator. Cover
multiple commands per process, all supported patterns, rejected tuples, help,
Stop retaining the prompt, acknowledgement without motion, EOF/quit/Ctrl+C,
startup failure and blocked/unconfirmed Stop. Do not require a web host.

Cover redirected line input with no prompts, SIGTERM and macOS SIGHUP as well
as the supported Windows console-close path. Shortened OS termination deadlines
must not start a second Stop writer or produce a false confirmed result. Use
platform signal/console lifetime hooks, not asynchronous ProcessExit cleanup.

Acceptance: no Web.Application/Web.Client/ASP.NET/Aspire transitive dependency;
no guest/session concepts or required web credentials; no successful stopping
claim from write acknowledgement alone; simulator survives client exit.

### M5. Web application composition and protocol conformance

Status: not started as a conformance task. M1 moved authority and controller
code into Web.Application and preserved focused hub/client tests, but the
runtime interruption, shutdown and full browser/API acceptance cases below
have not passed under the new transport and ownership model.

Depends on: M2 and M3; M4 supplies the competing-owner acceptance executable.

Owner/scope: Web.Api, Web.Application, Web.Client, Web.Contracts and the two UI
hosts. UI component design is not being changed.

Compose one application authority/controller/selected provider in Web.Api.
Move web operations and execution policy into Web.Application, not endpoints
or Primitives. Keep Web.Client only in the UI hosts. Retain the existing
SignalR endpoint and circuit-specific binding. Resolve the former ambiguous
grant metadata and Join proof as specified in the design: authoritative grant
times/sequences stay in server records; the private Join proof stays in the API
registry; public acknowledgements carry neither snapshots nor private proof.

Keep atomic limits, rolling rate limits, stopped recovery, bounded ordered
push, invitation rotation and unconfirmed I/O semantics. Restart discards all
authority and replaces the in-memory invitation, never resumes motion. The
same stopped-ready checks govern acknowledgement and subsequent Allow control.

The hosted device owner must invoke Runtime's shared bounded shutdown path
before transport teardown. Set normal HostOptions shutdown time above the
cleanup budget and test early cancellation explicitly. No request/circuit
disposal may release the process-wide owner.

Verification: move/reuse ControlAuthorityRevisionTests, InvitationLifecycleTests,
LimitsPolicyTests, GuestControlOperationsTests, ControlProtocolContractTests,
ControlHubPayloadTests, ControlHubContractTests and client lifecycle tests.
Exercise API startup with both UIs/AppHost absent, role-specific in-process
clients, CLI competition, actual envelope bounds, expiry closing connections,
and Stop/cycle while I/O remains unsettled. Focused browser checks retain
draft/send, guest loss, acknowledgement/paused recovery and notification rules.

Add API graceful-termination evidence showing Stop before disconnect and lock
release, matching the CLI's ordering rather than merely observing process exit.

Acceptance: no extra device-service hop, one authority/writer, no UI/provider
dependency in policy, private capabilities not exposed, and preserved CC,
SESSION, OUI and GUI requirements with explicit runtime evidence.

### M6. Defaults, provider artifacts and development arrangements

Status: not started as an operational acceptance task. M1 split host-neutral
Defaults from web hosting; the Cli still carries ASP.NET and both providers.
Profile-specific restore/publish artifacts, Simulated/Actual launch profiles,
collector-absent startup and configured export without Aspire remain unproved.

Depends on: M1; finish after M4/M5 to validate both real consumers.

Owner/scope: Defaults, Web.ServiceDefaults, executable composition/options,
build/publish properties, AppHost and launch/debug configurations.

Use host-neutral Defaults in Cli and through Web.ServiceDefaults in web hosts.
Share configuration names and precedence through standard hosting; retain
normal Logging and configured OpenTelemetry exporter settings. Console results
must not be mixed with logs. No startup path needs a running collector or
Aspire service discovery. Web-only instrumentation/health stays web-only.

Introduce `DeviceProvider=Stock|Simulator` (Simulator by default) to choose
project/package/TFM references before restore. Both Cli and Web.Api use the same provider-selection
contract. Default local development to Simulator; missing or mismatched selected
provider fails startup before device access. No runtime hot swap or fallback.
Stock omits simulator/native Rust assets; Simulator omits BLE packages, platform
workloads and permissions. Web UIs remain provider-independent.

Establish separate profile-specific artifacts/intermediate/restore paths before
SDK evaluation, so project.assets.json and generated outputs cannot cross
profiles. Build one profile per restore; switching profiles requires its own
restore. CI covers Windows/macOS times Stock/Simulator with required native
toolchains. A launch configuration must request its matching build profile,
not attempt to switch the already-restored dependency graph at runtime.

Use one AppHost with Simulated/Actual launch configurations that select the
matching profile and options. Simulated starts the native simulator as a
resource before Web.Api. Actual starts no simulator and acquires no BLE handle
itself. Preserve direct executable startup without AppHost and document how to
start a simulator plus CLI without simultaneously starting Web.Api.

Verification: reuse ServiceDefaultsRegistrationTests and TelemetryTests; inspect
resolved package graphs and published dependency manifests, not only DI types.
Run CLI/API logs and configured export with AppHost absent, plus startup with
the collector absent. Verify platform-specific profile builds in their native
CI environments. Orchestration smoke checks verify our configuration only,
not a new test suite for Aspire internals.

Acceptance: each promised minimal artifact contains only required providers;
Windows/macOS compatibility is evidenced separately; no stock-motion or hardware
support claim is inferred from a portable simulator build.

### M7. Stock-provider implementation and qualification boundary

Status: not started. Stock motion and native/physical qualification remain
deferred; hardware work requires separate explicit authorization.

Depends on: M2/M6 for provider isolation and platform targeting. Hardware work
requires separate explicit authorization; simulator migration can complete
while physical release remains blocked.

Move the already approved/pinned Shiny integration out of Cli into Stock.
Resolve native platform permission/callback/lifecycle issues inside the actual
owning Cli/Web.Api hosts before claiming support. Preserve the stock profile,
enrollment, strict codecs, observed-vs-requested values, knob scaling and
between-write revocation/settlement requirements. Implement no guessed physical
mapping and add no BLE helper process or replacement firmware.

Verification: deterministic protocol/provider tests first, selected-platform
build evidence second; separately authorized unloaded hardware tests prove the
DC qualification gates. Cached feedback has no periodic heartbeat: if the
required evidence cannot be obtained, fail closed and keep physical use blocked
rather than freshening caches or increasing timeouts to hide the problem.

Acceptance: physical release remains gated until native execution, mappings,
overrides and independent stopping are qualified. A deferred gate is not an
open architecture choice and is not passed by this plan.

### M8. Coordinated cutover, repository identity and documentation

Status: not started. Local solution/workspace/project identities changed in
M1, but old daemon/transport options and documentation remain until coherent
simulator acceptance. Hosted repository rename, publication and deployment are
separately authorized external writes. Coordinate the default log directory
rename and preserve historical source links until cutover.

Depends on: M1-M6 for simulator acceptance; M7 only for physical support claims.

Stop active owners with operator participation before cutover. Deploy coherent
new executable/provider/configuration sets; do not migrate live motion, grants
or sessions. First startup is paused and requires fresh explicit action.
Remove retired projects/solution entries, daemon IPC, stdio transport, obsolete
options, excluded copied sources and old namespace/package identities after
their surviving tests have moved. Never delete a failing behavioral test merely
because its old project is retired.

Include hosted repository rename to OssmSharp, owned URLs/badges/releases,
package metadata, workspace/solution names, docs, CI path filters and dev launch
references in the identity inventory. Remote rename, pushes, releases and
package publication remain separately authorized external writes; this task
does not grant them. Preserve upstream firmware/Rust and third-party identities.
Coordinate the default local log directory rename with operator setup, retaining
old logs or configuring their old location explicitly when continuity matters.

Recheck AGENTS.md against implemented boundaries and replace target/current
status in documentation only after acceptance evidence exists. Archive the old
plan as history; do not copy its passes into this plan. Record actual versions,
profile/OS, commands, passed/failed/deferred gates and residual risks per task.

Rollback requires stopping the new owner, restoring the previous coherent
binary/configuration set, and restarting paused with a fresh invitation. No
live-session migration, mixed provider binaries, automatic resumption or claim
that a code revert reverses device effects is permitted.

## Acceptance matrix

| Supported arrangement | Required proof |
| --- | --- |
| Cli + independently started simulator | No web/API/controller/AppHost dependency; persistent prompt; correct writes/status/interruption; strong Stop attempt on exit; simulator remains alive at zero. |
| Web.Api + simulator, UIs absent | Startup and API role tests work without UI/AppHost; one controller/device lifetime; no direct guest adapter access. |
| Full web + simulator | Existing invite/approve/send/Stop/limits/leave/cycle and interruption/acknowledgement scenarios survive moves. |
| Cli and Web.Api competing | Exactly one acquires the same machine-wide lease before device I/O, including shutdown and owner crash cases. |
| Selected-provider publish | Simulator has no Stock/BLE/native workload requirements; Stock has no simulator/Cargo requirements; options cannot select missing providers. |
| Logging and telemetry | Same relevant settings and behavior in CLI/web without Aspire; configured export works; collector absence does not prevent startup. |
| Stock modes | Build and native/physical qualification separately recorded; no simulator result substitutes for these gates. |

## Readiness and evidence limits

Architecture, names, CLI lifetime, ownership, simulator transport, dependency
profiles, defaults, grant metadata, Join proof and WIP compatibility are decided
in the design. M3 exposed one contract ambiguity: `SetMotionAsync` cannot
unambiguously distinguish Stop from a zero-speed settings change after some
external changes. Resolve the intended operation without guessing or weakening
Stop before claiming M2/M3 conformance.
Platform behavior and physical stopping remain measured acceptance/release gates.

Do not install packages, edit sibling checkouts, change firmware, create branches,
commit/push or run deployments without separate authorization. Full-suite or
expensive local validation needs approval; required CI gates remain required.
M0 documentation checks do not establish M1-M8 runtime acceptance.
