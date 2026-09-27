# OssmSharp direct-device migration plan

Date: 25 September 2026

Priority revised: 26 September 2026. The critical path is project restructuring,
stock device core, then a CLI control command sent to a real device over
Bluetooth. Simulator work follows, then web integration, then remaining work.

Status: implementation in progress. The recorded checkpoint has local Windows
instruction/project-graph evidence for M0-M1 and partial M2-M3 evidence; it does
not establish stock CLI acceptance. Milestone evidence below is historical,
not a live inventory of concurrent edits. The
[accepted design](../specs/web-control-design.md) owns decisions and contracts;
this file owns the ordered migration work and acceptance evidence. The
[previous extraction plan](web-control-implementation.md) retains historical
results, which are not passes for this migration. This plan owns implementation
priority; the accepted design owns runtime contracts, not a competing schedule.

## Scope and decisions

One computer, one device, one local admin/device user and one guest. Web.Api
owns the selected device when the web application runs. Cli is a persistent
interactive alternative owner, with no web controller, sessions, authentication
or service client. Detected external changes latch an interruption requiring
explicit local acknowledgement into paused state. Neither acknowledgement nor
reconnect resumes motion.

The CLI starts its simulator child only for an explicit `prompt --simulator <path>`
session, or attaches without owning a child for
`prompt --simulator tcp://127.0.0.1:<port>`; Web.Api uses an independently
started simulator. Both use bounded JSONL
over loopback TCP, and neither adapter owns a process. Stock and Simulator are alternative
build/publish profiles, selected before restore, and DI configures the one
available provider. Defaults is host-neutral; Web.ServiceDefaults adds web
hosting. AppHost is development/test convenience only. API/device lifetimes are
intentionally coupled; the UI hosts and CLI do not need to be deployed together.

The requester waived backwards compatibility for this WIP. Coordinate consumer
moves and remove old identities and interfaces without forwarding types,
namespace aliases, command shims, a daemon compatibility layer or legacy stdio
mode. Preserve valid behavioral regressions, not obsolete project layouts.
When replacing a legacy CLI path, move its still-required validation, command
and Stop assertions to the direct Stock path. Do not repair or restore the
obsolete simulator daemon solely to retain its old workflow or test harness.
Do not remove a failing behavioral test to hide a defect; migrate its subject
and preserve the assertion against the replacement implementation.
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
| Repository identity OSSMDotNet | OssmSharp. The checkout directory name is not proof of hosted repository identity; verify the hosted name and update owned remote/badge/package/release references only in an authorized publication step. |
| OSSMDotNet.slnx / OSSMDotNet.code-workspace | OssmSharp.slnx / OssmSharp.code-workspace; update project entries, relative paths, launch tasks and developer documentation. |
| OSSMDotNet.Abstractions | OssmSharp.Device.Primitives: IOssmDevice, values, enums and semantic device exceptions. |
| Shared DeviceBase, neutral options, lifecycle/Stop and interruption machinery | OssmSharp.Device.Runtime, extracted from OSSMDotNet, ControlCore and API/CLI code. No web policy, BLE, process launcher or Defaults dependency. |
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

## Historical baseline and evidence

Preserve existing work and recorded results. The legacy topology described here
is context to migrate away from, not behavior to restore or an execution order.

- At the migration baseline, the API hosted ControlService as a library and
  selected the simulator. The former four-process target was not implemented
  and is superseded. Do not build that obsolete service split first.
- At the migration baseline, DeviceCli used device libraries and a private
  per-user simulator host. The renamed Cli still uses that helper process,
  not an interactive REPL.
- At the migration baseline, SimulatedDevice started and owned a stdio child,
  while DeviceBase lived in the stock project and consumed stock-named options.
  M1 extracted DeviceBase and neutralized its options; M3 subsequently changed
  the native bridge and managed adapter to loopback TCP. Cli has not followed.
- Stock motion was deferred at baseline and is now the next core priority.
  The existing conditional macOS/Shiny CLI path
  only identifies a device; it does not prove a stock motion implementation or
  physical qualification.
- The old plan's passing test counts are historical. Establish a new focused
  baseline and record actual outcomes during implementation. Do not assume
  renamed tests or a successful solution build prove the new architecture.

## Ordered work

### Critical path and first scenario

This order supersedes the previous simulator-first schedule. Milestone IDs
remain stable for historical evidence; their numbers no longer define order.
Choose work from this sequence, not numeric milestone order or the next failing
legacy test. M2 is supporting work within Stock/CLI, not a separate all-consumer
gate between Stock and CLI.

1. **Project restructuring (M0-M1):** use the established Primitives, Runtime,
  Stock and Cli boundaries. Finish only restructuring required by the stock
  CLI path now; do not restart completed migrations or polish unrelated hosts.
2. **Stock device core (M7, with the required M2 mechanics):** implement BLE
  discovery/selection, connection, identity/service verification, command
  writes, feedback and disconnect. Bring forward the Stock targeting and
  provider-selection work needed from M6; full M6 acceptance is not a gate.
3. **CLI control of the real device (M4):** compose that core into the CLI and
  demonstrate the first scenario below. Do not substitute a read-only identify
  command, simulator pass or build success for this result.
4. **Simulator (M3):** complete independent transport and exercise the same CLI
  against it after the stock CLI scenario. Retain existing simulator evidence.
5. **Web integration (M5):** compose and verify the web consumers, including the
  remaining web-specific M2 ownership/interruption integration.
6. **Remaining work (M6, M8):** complete the broader
  platform/provider matrix, telemetry, AppHost arrangements and cutover.

Physical release qualification is separately authorized and recorded outside
software migration acceptance; the first live CLI command is a functional
scenario, not full physical qualification.

The next unfinished target is Stock connection and command transport, followed
by CLI control. An identity-only provider or prompt that rejects every motion
command is an intermediate state, not a completed phase. Continue to supported
command dispatch without diverting into simulator or web functionality.

**First acceptance scenario:** under separate explicit hardware authorization,
with the device unloaded and the operator able to cut power independently,
start the CLI without simulator, web hosts or AppHost. Discover/select the
intended real device; connect over BLE and verify
its identity and required GATT service; submit an explicit supported control
command from the CLI; verify the stock provider sent the corresponding GATT
write and record the device's response or observed state separately from local
dispatch; then Stop and disconnect through the owning session. Discovery or
identity reading alone is not completion. Record the OS, command and actual
outcome; mocked transport checks cannot establish a real-device pass.
Prove the applicable identity, ownership, command validation, Stop and cleanup
paths with deterministic tests before a live attempt. A successful GATT write
alone does not prove observed motion or physical stopping.

Only prerequisites needed by this scenario belong on the critical path.
Resolve shared command/Stop semantics in Stock/Runtime as part of M7/M2, not
as a simulator project. Do not wait for full simulator, web, AppHost, telemetry
export or cross-platform release acceptance to implement the stock CLI path.
Required device checks remain with the commands they protect; broader release
qualification is a separate result, not a reason to defer BLE implementation.
Repository restrictions on agent-operated hardware remain an execution
boundary: complete the Stock/CLI software and deterministic checks, then report
the precise blocked live check and operator prerequisites. Do not mark M4
accepted or start simulator/web work because hardware execution is blocked;
request an explicit priority change before departing from this sequence.

The evidence checkpoint predates ongoing Stock/CLI implementation. It must not
cause an agent to restart completed work or revert concurrent changes. Each
task includes its focused validation;
behavior changes must first have a regression failing for the intended reason.
Use existing xUnit v3/MTP v2 projects, FastMoq where suitable, AwesomeAssertions,
TimeProvider fakes and shared deferred-I/O fixtures. No new test framework or
dependencies are authorized by this plan.

### M0. Align repository instructions before code

Depends on: separate implementation authorization.

Recorded evidence: completed on 25 September 2026 for the direct-owner
topology. The 26 September correction distinguishes hardware execution
permission from software development in root instructions; task order stays
in this plan. At the earlier checkpoint, focused instruction content/link and
diff-format checks passed; an independent
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
- Put mechanical interruption/Stop rules in Runtime. Local admin equals
  device user. Physical override acknowledgment
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
accepted CLI/DI/lifetime model or the Stock-first sequence. Simulator-only
execution restrictions must not prohibit Stock software implementation. Verify
names, local links and relevant instruction
frontmatter; do not mark runtime checks passed from this editorial task.

### M1. Characterize and establish the target dependency graph

Recorded evidence: complete for the local project graph on 26 September 2026,
not full migration acceptance. These paragraphs record incremental checkpoints,
not new work to repeat. The slices extracted
`DeviceBase`, its shared logging and provider-neutral `DeviceRuntimeOptions`
into `OssmSharp.Device.Runtime`, device contracts and values into
`OssmSharp.Device.Primitives`, and stock codecs/provider into
`OssmSharp.Device.Stock`. Simulator consumes only Primitives and Runtime;
Web.Application does not reference Stock. Primitives contract tests passed 14,
Runtime tests 40, Stock tests 151, CLI Identify tests 11, CLI simulator-host
tests 17 and native Simulator integration tests 72. Focused Runtime, Simulator,
then-ControlService and CLI builds passed on Windows. All 30 local solution
projects resolve without cycles. CLI still references
both Stock and Simulator; M7/M4 must bring forward M6's provider selection
before restore for the stock CLI path. Stock's
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
Aspire or service-discovery dependency. CLI composition and startup without a
collector or AppHost belong to M4; configured export remains M6. Capability-token ownership
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
.NET SDK 10.0.401. Focused unit checks passed 41 and 22, and real URL-override
process cases passed 2.
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
pass under new names. Tests for paths retired in M4 or M3 migrate with those
phases; do not repair obsolete workflows to keep their original harness passing.
Verify direct and transitive references, not just source
imports. Repository-rename publication is not performed here.

### M7. Stock device core and BLE command transport

Recorded evidence: no accepted stock command path at the ordering checkpoint.
This is the next implementation priority after M1. Preserve and continue any
in-progress Stock work; identity codecs and an identify-only path do not
establish a working stock control provider.

Depends on: M1. Implement the M2 runtime mechanics and M6 Stock host targeting,
provider selection and restore isolation needed by this path within this phase.
Neither full M2/M6 acceptance nor simulator or web completion is a prerequisite.

Owner/scope: Device.Stock, Device.Primitives, Device.Runtime and the native
targeting/composition needed by Cli. Keep Shiny and GATT details in Stock.

Implement the Stock build profile and isolated restore/output paths needed by
the selected native host here. Its resolved dependencies must exclude Simulator
and Rust. Use the pinned BLE package; unavailable native tooling is a reported
blocker, not a reason to substitute a simulator or silent nonfunctional provider.
Build the Stock project and then the CLI in M4, not the whole solution with a
Stock profile. Web.Api and AppHost Stock composition belong to M5 and M6.

Move the already approved/pinned Shiny integration out of Cli into Stock.
Implement bounded discovery, explicit device selection, connection, live identity
and required service/characteristic verification, supported control-command
writes, state feedback and disconnect. Resolve the current host's native
permissions, callbacks and lifecycle for the actual CLI, not a substitute host.
Resolve Stop versus zero-speed configuration at the shared contract before
implementing ambiguous writes. Preserve the stock profile, strict
codecs, observed-vs-requested values, knob scaling and between-write
revocation/settlement requirements. The 26 September CLI command decision
removes separately enrolled Wi-Fi MAC verification; keep the selected peripheral,
firmware, GATT and bounded payload checks, and report the MAC as device
information. Add no BLE helper process, guessed physical
mapping or replacement firmware.

Verification: deterministic tests at the existing BLE boundary must cover
selection, failed connection/identity checks, exact supported GATT commands,
feedback and cleanup. Build the provider for the current native platform. M4
owns compiling and exercising the actual Stock-targeted CLI; M7 does not
require a completed CLI or any simulator to prove its provider code path.

Acceptance: Stock provides connection and control-command operations that the
CLI can consume without web or simulator dependencies. M4 owns the first real
CLI-to-device control-command scenario. Discovery/identity alone is insufficient.
Record later native/physical release qualification separately; retain DC gates
without postponing implementation until simulator or web acceptance.

### M2. Runtime support within the Stock and CLI phases

Status: in progress on 26 September 2026; **not accepted**. DeviceBase shares
one disposal outcome and bounds Stop, connection settlement, issued-write
waiting and core cleanup within five seconds. These checks do not prove the
full M2 safety contract.

Windows/macOS native behavior and delayed-cleanup evidence still require a
dedicated native CI environment. The sole Runtime interruption latch,
stale-revision acknowledgement, shared Stop path and between-write revocation
checks are not implemented. Existing CLI daemon and API disposal evidence
cannot substitute for those requirements.

Depends on: M1. Implement the mechanics needed by the stock command path with
M7 before M4 uses them. Web projection and the full native platform acceptance
matrix follow in M5/M6; they do not block stock-core coding.

Scope now: the single I/O owner, interruption revision, explicit Stop
semantics, between-write revocation and bounded cleanup needed by Stock and
Cli. Scope later: Web.Application projection in M5, then the remaining native
matrix in M6. The full M2 acceptance below is not a
prerequisite for starting M7 or composing M4; its applicable runtime protections
must be in place before the operations they protect.

Owner/scope: DeviceBase and device options, common operation
settlement/Stop coordination and semantic error publication.

Extract the single I/O gate and minimal interruption revision into Runtime.
Preserve no-full-state-cache DeviceBase behavior; each consuming owner retains
the state snapshot it needs. Reject new nonzero operations synchronously on
Stop/interruption/shutdown; discard pending motion and recheck revocation
between actual transport writes. Keep the web controller's desired-state slot
and guards, but do not create a second runtime queue or import grants into Cli.

Runtime owns the only interruption latch and revision. In M5, Web.Application
projects that state and adds role/authority-revision checks before Runtime acknowledgement;
it must not keep a separately clearable device lock. Reuse one acknowledged
paused outcome across web and CLI paths.

Detected external changes raise the common semantic event and latch inhibition.
In M5, Web.Application revokes guest authority. In M4, Cli reports and accepts only
non-motion commands until explicit acknowledgement. Acknowledge verifies the
current revision and stopped baseline; it never resumes. Missing/late feedback
remains unknown/faulted, not a fabricated override or confirmation.

Verification now: reuse adapter expectation tests; add deterministic
Stop-vs-issued-write, stale-ack, failure-before-connect and
cleanup checks for the Stock/CLI path. Verification in M5: reuse
ControlExecutionTests/ExecutionOwnershipTests and CoreRig/FakeBridge for web
projection and CLI integration. Complete remaining native crash and
delayed-cleanup cases in M6. Do not require those later consumers to exist
before verifying the Stock/CLI mechanics.

Acceptance: one writer in each consumer, no web-policy dependency in Runtime/Cli,
zero-speed priority without concurrent I/O, no replay after
interruption/reconnect, and explicit unknown outcomes on cleanup timeout.

### M4. Interactive direct-device CLI and first real control command

Recorded evidence: no accepted foreground Stock CLI at the ordering checkpoint.
M1 renamed the executable/tests but retained daemon/IPC, launcher ownership,
ASP.NET and both providers. Continue current CLI changes toward the stock BLE
control path; this record is not an instruction to restore those legacy parts.

Depends on: M1 and M7's stock core, including the required M2 mechanics and
Stock build configuration. No M3 or M5 dependency. Complete the first real
control-command scenario before expanding simulator and web integration.

Owner/scope: Cli entry point, DeviceCommand/runner, device selection/options and
the existing DeviceCli unit/integration tests.

Replace per-command daemon IPC with the design's foreground prompt. Keep one
stock device connection across help, status, set, Stop and acknowledgement. Reuse
pinned System.CommandLine parsing; commands have three numeric inputs and
pattern, with provider capabilities and model validation enforced. Help/version
require no connection. Invalid input returns to the prompt without sending work.
Wire the selected Stock provider directly; do not require simulator startup,
web credentials, web policy or AppHost. Use host-neutral Defaults and bring
forward only the M6 composition/build work needed for this executable.

Implement and deterministically verify the command and required session
lifecycle together, then attempt the live CLI-to-device scenario as authorized.
Do not redefine the first result as scanning, identification, a mock write,
an always-rejecting prompt or simulator control. Do not claim completion while
supported commands still cannot reach the Stock provider.

All handled exits share the five-second-default cleanup budget: stop intake,
invalidate pending work, wait for issued I/O, attempt serialized zero, then
disconnect. Only retry zero after actual settlement and within the budget.
Report unconfirmed cleanup, preserve cancellation exit semantics, and do not
open a replacement connection before cleanup settles. Move required bounds,
validation and Stop assertions to the direct CLI/provider tests, then remove
SimulatorHost/IPC, launcher code and the obsolete executable-path shim in this
phase. Do not restore legacy simulator motion to satisfy the old harness. Do
not postpone replacement/removal until simulator acceptance or M8 cleanup.

Verification: reuse DeviceCommandTests and in-process session tests with a
controlled BLE boundary for deterministic command/lifecycle cases. Use
DeviceCliProcessTests for no-hardware scenarios such as help/version, invalid
usage and startup failure.
Record the actual native CLI-to-device command result separately. Cover multiple commands,
supported patterns, rejected tuples, help, Stop retaining the prompt,
acknowledgement without motion, EOF/quit/Ctrl+C, startup failure and
blocked/unconfirmed Stop. Simulator process cases are a later M3 deliverable.

Cover redirected line input without prompts, SIGTERM and macOS SIGHUP, and the
supported Windows console-close path. Shortened OS termination deadlines must
not start a second Stop writer or produce false confirmation. Use platform
signal/console lifetime hooks, not asynchronous ProcessExit cleanup. Validate
the current host first and record remaining native platform checks separately.

Acceptance: the first real-device CLI control-command scenario is demonstrated;
no Web.Application/Web.Client/ASP.NET/Aspire or simulator requirement on the
Stock CLI path; no guest/session concepts or required web credentials; no
successful stopping claim from write acknowledgement alone.

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

The historical CLI daemon/executable-path flow has been replaced with an
explicit CLI-owned simulator child and foreground prompt. This implementation
has compiled on Windows but has not run or passed the simulator CLI acceptance
scenario. The temporary `SimulatedDeviceOptions.ExecutablePath` member remains
debt until its remaining consumers are removed. Stop versus an
intentional zero-speed configuration write is indistinguishable after some
external changes under the current device contract. Resolve that contract
question before dependent motion work. AppHost readiness only checks socket
acceptance. Listener/handshake deadlines, exact 4 KiB CRLF framing and CLI exit
at zero belong to M3; API exit evidence belongs to M5; AppHost readiness and
remaining native-platform evidence belong to M6.

Depends on: M4's first stock CLI control-command scenario. Reuse M1/M2 runtime
and CLI work; preserve existing simulator progress but resume it after that
scenario. M3 completion is not a prerequisite for Stock or Cli implementation.

Owner/scope: tools/ossm-sim-bridge transport/framing/protocol, SimulatedDevice
connection lifetime, options and native/.NET integration fixtures.

Extend Cli's provider selection and isolated restore/output paths to Simulator
in this phase. Its resolved dependencies must exclude Stock/BLE and associated
native workloads. Reuse the M7/M4 selection contract; do not wait for M6.

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
Remove any remaining legacy stdio mode within M3; do not ship both models or
defer transport retirement to M8.

Verification: reuse framing.rs/protocol.rs tests and SimulatedDeviceTests with a
test-owned server process. Integrate the existing foreground CLI with the
Simulator profile; never reintroduce the daemon/IPC command path. Cover
partial/oversized frames, delayed writes,
malformed replies, independent startup, refusal of a second client, frozen
feedback, injected external changes, disconnect while work is pending and
successive clients. Closing CLI must leave the simulator process alive and at
zero. M5 verifies the same requirement for API exit; API/web completion is not
an M3 acceptance gate. Kill only test-owned processes in teardown.

Acceptance: independent simulator plus foreground CLI completes commands and
exit-at-zero checks; runtime provider requires an endpoint, not an executable
path; no adapter Process.Start/Kill or child job ownership; no BLE or web
dependency. Do not wait for M5/M6 to accept this provider/CLI arrangement.

### M5. Web application composition and protocol conformance

Status: not started as a conformance task. M1 moved authority and controller
code into Web.Application and preserved focused hub/client tests, but the
runtime interruption, shutdown and full browser/API acceptance cases below
have not passed under the new transport and ownership model.

Depends on: M4's stock CLI scenario, then M3. Complete web-specific M2
integration here using the CLI and API tests for their separate compositions.

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
cleanup budget and test early cancellation explicitly. Request/circuit
disposal must not bypass the shared cleanup path.

Verification: move/reuse ControlAuthorityRevisionTests, InvitationLifecycleTests,
LimitsPolicyTests, GuestControlOperationsTests, ControlProtocolContractTests,
ControlHubPayloadTests, ControlHubContractTests and client lifecycle tests.
Exercise API startup with both UIs/AppHost absent, role-specific in-process
clients, actual envelope bounds, expiry closing connections,
and Stop/cycle while I/O remains unsettled. Focused browser checks retain
draft/send, guest loss, acknowledgement/paused recovery and notification rules.

Add API graceful-termination evidence showing Stop before disconnect, matching
the CLI's ordering rather than merely observing process exit.
With the Simulator profile, API exit must leave the independent simulator alive
at zero. Apply the existing provider-selection contract to Web.Api here; keep
the broader artifact/platform matrix in M6.

Acceptance: no extra device-service hop, one authority/writer, no UI/provider
dependency in policy, private capabilities not exposed, and preserved CC,
SESSION, OUI and GUI requirements with explicit runtime evidence.

### M6. Defaults, provider artifacts and development arrangements

Status: not started as an operational acceptance task. M1 split host-neutral
Defaults from web hosting; the Cli still carries ASP.NET and both providers.
Profile-specific restore/publish artifacts, Simulated/Actual launch profiles,
collector-absent startup and configured export without Aspire remain unproved.

Depends on: M5 for this remaining operational phase. The Stock CLI targeting,
provider selection, isolated restore paths and host-neutral configuration needed
by M7/M4 are delivered there first, not deferred behind full M6 acceptance.

Owner/scope: Defaults, Web.ServiceDefaults, executable composition/options,
build/publish properties, AppHost and launch/debug configurations.

Use host-neutral Defaults in Cli and through Web.ServiceDefaults in web hosts.
Share configuration names and precedence through standard hosting; retain
normal Logging and configured OpenTelemetry exporter settings. Console results
must not be mixed with logs. No startup path needs a running collector or
Aspire service discovery. Web-only instrumentation/health stays web-only.

Complete verification of `DeviceProvider=Stock|Simulator`, introduced for the
Stock path in M7/M4, extended to Simulator in M3 and Web.Api in M5. Choose
project/package/TFM references before restore. Both Cli and Web.Api use the same
provider-selection contract. The design's default Simulator profile is a
configuration default, not implementation priority; M7/M4 explicitly select
Stock and never fall back to Simulator. Missing or mismatched selected
provider fails startup before device access. No runtime hot swap or fallback.
Stock omits simulator/native Rust assets; Simulator omits BLE packages, platform
workloads and permissions. Web UIs remain provider-independent.

Verify the profile-specific artifacts/intermediate/restore paths established
with Stock in M7 and extended with Simulator in M3. They must be set before SDK
evaluation, so project.assets.json and generated outputs cannot cross profiles.
Build one profile per restore; switching profiles requires its own
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

Separate physical release qualification, outside software migration acceptance:
separately authorized unloaded
hardware checks must prove the DC gates for native execution, mappings,
overrides and independent stopping. Cached stock feedback has no periodic
heartbeat; missing evidence must fail closed, not be hidden by freshened caches
or increased timeouts. M4's first live control-command result is not full
physical qualification. Record those outcomes separately without moving Stock
implementation back behind simulator or web work.

### M8. Coordinated cutover, repository identity and documentation

Recorded evidence: final cutover is not accepted. Local solution/workspace/
project identities changed in M1. M4 removes the legacy CLI/daemon options;
M3 completes simulator transport retirement. Do not retain or restore those
paths until this final phase. Hosted repository rename, publication and deployment are
separately authorized external writes. Coordinate the default log directory
rename and preserve historical source links until cutover.

Depends on: the stock core and CLI scenario (M7/M4), then simulator (M3), web
integration (M5) and remaining operational work (M6). Record broader physical
qualification separately before physical release claims; no phase is accepted
merely because the plan was reordered.

Stop active owners with operator participation before cutover. Deploy coherent
new executable/provider/configuration sets; do not migrate live motion, grants
or sessions. First startup is paused and requires fresh explicit action.
Finish removing retired projects/solution entries, excluded copied sources and
old namespace/package identities after their surviving tests have moved.
Verify that M4 already removed daemon IPC and obsolete CLI options and M3
already removed legacy transport. Do not defer a critical-path removal here.
Never delete a failing behavioral test merely because its old project is retired.

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
| **First: Cli + real Stock device over BLE** | Without simulator, web hosts or AppHost: select/connect to the intended device, submit a supported control command from the CLI, record the corresponding GATT write and real response/observed state, then Stop/disconnect. Discovery or identity alone does not pass. |
| Cli + CLI-owned simulator child | No web/API/controller/AppHost dependency; persistent prompt; correct writes/status/interruption; strong Stop attempt on exit; settle device cleanup before stopping the child. |
| Web.Api + simulator, UIs absent | Startup and API role tests work without UI/AppHost; one controller/device lifetime; no direct guest adapter access. |
| Full web + simulator | Existing invite/approve/send/Stop/limits/leave/cycle and interruption/acknowledgement scenarios survive moves. |
| Selected-provider publish | Simulator has no Stock/BLE/native workload requirements; Stock has no simulator/Cargo requirements; options cannot select missing providers. |
| Logging and telemetry | Same relevant settings and behavior in CLI/web without Aspire; configured export works; collector absence does not prevent startup. |
| Stock modes | Build and native/physical qualification separately recorded; no simulator result substitutes for these gates. |

These rows are cumulative migration evidence, not prerequisites for the first
row. M7/M4 prove their required Stock dependency isolation and cleanup before
the live command; M3 proves simulator/CLI; M5 proves web; M6 completes
cross-platform/artifact/telemetry evidence.
Physical release qualification is recorded separately. The first command does
not imply all rows passed.

## Readiness and evidence limits

Architecture, names, CLI lifetime, ownership, simulator transport, dependency
profiles, defaults, grant metadata, Join proof and WIP compatibility are decided
in the design. M3 exposed one contract ambiguity: `SetMotionAsync` cannot
unambiguously distinguish Stop from a zero-speed settings change after some
external changes. Resolve the intended operation without guessing or weakening
Stop as part of M7/M2 before dependent stock writes; this is not a requirement
to finish simulator work first.
Platform behavior and physical stopping remain measured acceptance/release gates.

Do not install packages, edit sibling checkouts, change firmware, create branches,
commit/push or run deployments without separate authorization. Full-suite or
expensive local validation needs approval; required CI gates remain required.
M0 documentation checks do not establish M1-M8 runtime acceptance.
