# Web-control implementation plan

Date: 22 September 2026

Status: Tasks 1-6 are implemented and focused-verified. This is the authoritative task list for the
[web-control design](../specs/web-control-design.md). Recorded focused evidence
does not establish full design conformance or physical qualification.

## Goal and scope

Separate the SignalR host into `OSSMDotNet.Api` (now `OSSMDotNet.ApiService`), make
`OSSMDotNet.ControlService` the transport-neutral application library, and keep
device execution independently usable in ControlCore. AdminWeb and GuestWeb
remain separate Blazor hosts using the existing ControlClient contract. Api
hosts the library and sole device owner; it is not a fourth application service.

No REST surface, new dependency, physical-device work, broad validation cleanup
or unrelated naming refactor is included. Preserve the device-ownership lease's
existing exclusion scope through the host rename.

## Confirmed decisions

- The requester selected coordinated updates to repository .NET consumers,
  without source/binary compatibility shims for moved types. Preserve SignalR
  methods, wire representations and error semantics during extraction.
- The requester selected **10 authenticated run-input attempts in any rolling
  monotonic second**, replacing the existing 20-capacity bucket as an explicit
  behavior correction. Preserve authenticated rejected-attempt accounting and
  the Stop/Leave exemptions.
- Reuse the existing device contracts, application operation seam, client,
  shared widgets and tests. Do not introduce a command bus, duplicate authority,
  generic transport framework or replacement device bridge.

## Dependencies and current evidence

The target call path is web hosts → ControlClient → Api → ControlService →
ControlCore → device abstractions. Api selects the concrete adapter at its
composition root. Models remains shared immutable data; operation interfaces
belong with their operation owner. Neither web host resolves application
authority or a device implementation.

The 22 September baseline verified that unresolved legacy
`IDeviceBridge`/`SimulatorProcessBridge` consumers prevented ControlCore from
compiling. Task 1 removed that obsolete path in favor of `IOssmDevice`.

Task 1 precedes tasks 2 and 3; both precede task 4. Task 5 follows task 4,
and task 6 follows task 5. Tasks 2 and 3 currently touch the same engine and
tests, so perform them sequentially unless file ownership is separated first.
Library conversion and host extraction may share one coordinated working
increment; intermediate states need not be independently deployable.

## Tasks

### 1. Establish the device baseline

**Status:** Complete on 23 September 2026.

- **Owner/scope:** Core and device composition; `ControlEngine.cs`,
  `DeviceLoop.cs`, service registrations and affected project references.
- **Work:** Reconcile obsolete bridge consumers with the existing `IOssmDevice`
  adapters, including feedback, lifetime and privileged simulator controls.
  Follow the [adapter design](../specs/device-control/adapters.md); do not
  recreate the obsolete bridge or broaden into physical BLE integration.
- **Acceptance:** Affected projects build against the supported simulator path.
  One writer retains unsettled I/O ownership; fresh device observations drive
  readiness; Stop cannot race a second write.
- **Verification:** Establish the focused build baseline, then use deterministic
  core/device tests and the affected simulator integration checks. For changed
  behavior, first prove a focused regression fails for the intended reason.
- **Evidence:** The native simulator helper release build completed. Focused MTP
  projects passed: `OSSMDotNet.Tests` 204/204,
  `OSSMDotNet.Simulator.IntegrationTests` 72/72, `ControlCore.Tests` 89/89,
  `ControlService.Tests` 170/170, and sequential
  `ControlService.IntegrationTests` 65/65. Regressions first failed for stock
  profile readiness, knob-scaled effective speed with live position, stale
  reconnect observations, and a pending speed write accompanied by an
  unexplained depth change. The pinned stock firmware evidence and agent test
  constraints are retained under `.docs/references/device-control/`.
- **Remaining limits:** Stock BLE transport and physical qualification remain
  deferred. Simulator evidence does not populate the physical allowlist or
  prove stopping distance, BLE latency, or independent stopping.

### 2. Correct the rolling input limit

**Status:** Complete on 23 September 2026.

- **Depends on:** Task 1.
- **Owner/scope:** Application policy currently in `ControlEngine.cs`;
  `ControlEngineTests.cs` and `LimitsPolicyTests.cs`.
- **Work:** Prove an eleventh authenticated attempt within a rolling second is
  rejected, then replace the token bucket. Update the existing capacity-20 test
  to the approved contract rather than remove the test.
- **Acceptance:** Boundary and burst cases obey the rolling limit using monotonic
  time. Authenticated invalid attempts still consume capacity; rejected excess
  work cannot advance sequence, renew authority or replace pending work. Status,
  Stop and Leave remain exempt.
- **Verification:** Controlled-clock tests around the rolling boundary, idle
  bursts, wall-clock jumps, rejected attempts and unchanged motion deadlines.
  Keep this behavior correction distinct from project/file moves.
- **Evidence:** The old capacity-20/refill tests failed against the approved
  contract before implementation. The controller now records at most ten
  authenticated attempts in a bounded monotonic rolling second. Focused tests
  cover the exact boundary, wall-clock jumps, authenticated engine and
  transport-level invalid requests, bound null payloads through the real hub,
  unchanged pending work and grant deadlines, and Stop/Leave exemptions.
  `ControlCore.Tests` passed 92/92, `ControlService.Tests` 172/172, and
  sequential `ControlService.IntegrationTests` 66/66.

### 3. Establish atomic core operations

**Status:** Complete on 23 September 2026.

- **Depends on:** Task 1; coordinate shared engine edits with task 2.
- **Owner/scope:** ControlCore controller contracts and `ControlEngine.cs`;
  the affected core tests, particularly `LimitsPolicyTests.cs`.
- **Work:** Expose arm/limit operations that atomically check and commit
  execution-side preconditions. Do not replace them with snapshot reads followed
  by separate writes, or copy pending device state into the application layer.
- **Acceptance:** Rejection leaves intent and limits unchanged. Stop wins,
  delayed writes retain ownership, and recovery leaves paused. The execution
  boundary can be exercised without creating a guest session. Application-to-core
  lock acquisition stays one-way; callbacks/publication occur outside locks.
- **Verification:** Deterministic tests for in-flight and awaiting-feedback
  conflicts, arm preconditions, stale revisions, Stop races and logical timeout
  without I/O settlement. Preserve DC-01 through DC-09 by reference.
- **Evidence:** Core arming is usable without a guest session; application Arm
  and AllowControl atomically verify the exact unexpired approved admission.
  Limits and arming reject unsettled and logically timed-out I/O without
  replacing its writer. Device calls are reserved under the core lock, claimed
  once after final authorization, and invoked outside the lock; synchronous
  callbacks cannot block Stop. Disposal waits at most two seconds and reports
  unsettled I/O rather than claiming settlement. Focused projects passed:
  `ControlCore.Tests` 95/95, `ControlService.Tests` 174/174, and sequential
  `ControlService.IntegrationTests` 66/66.

### 4. Extract application authority into ControlService

**Status:** Complete on 23 September 2026.

- **Depends on:** Tasks 2 and 3.
- **Owner/scope:** `ControlAuthority.cs`, `ControlOperations.cs`,
  `GuestControlOperations.cs`, session/grant state currently in the engine, and
  `Models/IControlOperations.cs` with its repository consumers.
- **Work:** Move invitations, admissions, policy versions, grants, sequences,
  application deadlines and scoped projections into one serialized authority.
  Separate guest operations from the SignalR registry and concrete simulator.
  Move operation interfaces to their owner without compatibility shims.
- **Acceptance:** Application authority is testable without ASP.NET or SignalR;
  ControlCore needs no remote session. Atomic core rejection leaves associated
  policy/grant state unchanged. No competing authority or execution path remains.
- **Verification:** Reuse/move `InvitationLifecycleTests`, applicable
  `LimitsPolicyTests`, `ControlAuthorityTests`, `ControlAuthorityRevisionTests`
  and `GuestControlOperationsTests`. Prove exact admission, stale-proof rejection,
  rotation during unsettled I/O, expiry, and policy changes without lease extension.
- **Evidence:** `ControlAuthority` now owns invitation proofs, connection-bound
  admissions, session expiry, policy versions, grants, sequences, rolling input
  attempts and role-scoped projections under one lock. `ControlEngine` exposes
  only execution state and atomic arm, limits, Stop, unlock and accepted-motion
  operations; accepted motion carries the original monotonic grant deadline and
  no guest session state. `GuestControlOperations` is independent of SignalR and
  its connection registry; the hub retains private proofs and performs transport
  binding. The operation interface moved from Models to ControlService without a
  forwarding type. Execution-owned tests remain in Core and application-owned
  scenarios moved to Service; delayed-write cycle serialization and owned-ID
  wrong-tuple lockout are retained explicitly. The focused projects passed:
  `OSSMDotNet.Tests` 205/205, `OSSMDotNet.Simulator.IntegrationTests` 72/72,
  `ControlCore.Tests` 50/50, `ControlService.Tests` 214/214, and sequential
  `ControlService.IntegrationTests` 66/66.

### 5. Extract Api and cut over composition

**Status:** Complete on 23 September 2026.

- **Depends on:** Task 4; coordinate library/project conversion in this increment.
- **Owner/scope:** New `OSSMDotNet.Api`; existing `Program.cs`, authentication,
  listeners, hub, connection registry, push dispatcher and ownership lifetime;
  AppHost, solution/project references and friend assemblies.
- **Work:** Move hosting and delivery to Api; make ControlService a class library.
  Keep application invalidation decisions in the library, not inferred again
  from UI state strings in delivery code. Preserve configuration keys, hub path
  and circuit-scoped clients. Move tests to the assembly that owns their subject.
- **Acceptance:** Api owns one library/controller/device lifetime. Three service
  processes remain. A competing old/new host cannot acquire device ownership;
  the lease lasts through managed shutdown. Guest and operator service roles,
  proof binding, bounded push and cancellation/unknown-outcome semantics remain.
- **Verification:** Focused auth, protocol, registry and dispatcher tests; real
  local SignalR contract/push/loss checks; listener-override and competing-owner
  evidence. Existing same-process semaphore/options tests alone do not establish
  cross-process exclusion or actual socket exposure. Keep unit and integration
  projects named after their owning assembly and reuse shared fakes.
- **Evidence:** `OSSMDotNet.Api` is the only control host; ControlService is a
  class library, and AppHost builds with Api under the existing `control`
  resource. The old host copies and project exclusions were removed; host and
  authority tests live with their owners. Two real Api processes demonstrate
  competing-owner rejection during managed shutdown and acquisition after
  release. Process checks also verify that conflicting `ASPNETCORE_URLS` and
  `DOTNET_URLS` values open no additional listener. The lease outlives async
  host disposal. Focused MTP projects passed: `Api.Tests` 117/117,
  `Api.IntegrationTests` 67/67, `ControlService.Tests` 97/97,
  `ControlService.IntegrationTests` 2/2, and `ControlClient.Tests` 56/56.
  This proves new/new process exclusion on Windows, not old/new binary coexistence
  or physical stopping.

### 6. Verify consumers and complete documentation

**Status:** Complete on 23 September 2026.

- **Depends on:** Task 5.
- **Owner/scope:** ControlClient, AdminWeb/GuestWeb tests, focused browser checks,
  AppHost composition and `.docs/diagrams/architecture.html`.
- **Acceptance:** Invite → approve → Send → Stop works through both web hosts.
  Draft edits survive refresh without being sent; loss clears authority and
  requires fresh admission. Restart, reconnect and new invitations never arm
  motion. The device CLI uses the standalone device libraries, not the service.
- **Verification:** `HomePageBehaviorTests`, `JoinPageBehaviorTests`,
  `GuestCircuitHandlerTests` and client lifecycle tests first; existing browser
  module checks next; then the focused integrated simulator journey. Check
  applicable OUI/GUI requirements and device CLI behavior. Use Aspire for
  application orchestration, not a custom launcher.
- **Documentation:** Update the diagram to show Api hosting ControlService only
  after cutover works. Record actual evidence, unresolved conformance gaps and
  task completion here; do not retain a second checklist in the design.
- **Evidence:** The final focused MTP runs passed `AdminWeb.Tests` 15/15,
  `GuestWeb.Tests` 26/26, `Cli.Tests` 21/21 (now retired),
  `DeviceCli.Tests` 12/12, and `DeviceCli.IntegrationTests` 8/8.
  `ControlClient.Tests` passed 56/56 at
  cutover. With Python 3.13.15 in the local uv environment, the guest browser
  modules passed 6/6 and lifecycle modules 2/2. The isolated Aspire
  `control`, `admin` and `guest` services became healthy; focused operator
  layout and the full Edge simulator browser journey passed, including
  invitation, approval, Send, Stop, non-pausing limits, leave/rejoin, End,
  cycle revocation, second-guest rejection, guest socket loss, and operator
  recovery. Component regressions separately prove delivery resync ends the
  guest and closes its connection before the server timeout, while unexpected
  loss retains its inline danger alert. The diagram shows
  Api hosting ControlService as a library. The first combined browser-module
  run had one intermittent blur assertion failure; isolated and subsequent
  combined runs passed. That test timing remains a residual reliability risk,
  not evidence of a verified production fix.

## Risks and blocked conformance work

- **Grant metadata:** Current DTOs lack the draft's grant sequence and explicit
  timestamp fields, although issuance-based expiry exists internally. The project
  owner must settle logical-versus-wire requirements before a separate versioned
  protocol change. This does not block compatible extraction; it blocks claiming
  complete grant-schema conformance. Do not add fields silently to closed schemas.
- **Join receipt:** The internal operation returns a private proof that is bound
  server-side; the hub returns an acknowledgement. Clarify that boundary in the
  design instead of exposing a secret merely to match ambiguous wording.
- **Existing assurance gaps:** Inspect logical-envelope versus full-frame size
  checks, real SignalR closed-schema rejection and expiry-triggered socket closure.
  Rejection tests and a substituted hub fixture are not proof of full host
  conformance. Keep necessary boundary checks with their owning task and identify
  behavior changes separately from extraction.
- **Cutover:** Never run two authority hosts against the same device. Restart
  discards guest authority; rollback also requires stopping the managed owner.
  Ask the operator to stop any existing process that blocks the work.

## Completion and deferred checks

Run focused checks alongside each change. Use xUnit v3/MTP v2,
AwesomeAssertions and FastMoq where useful, retaining direct value tests and
deterministic device/clock fakes. Prove behavior regressions fail before fixing
them. Documentation-only changes need content/link checks, not application runs.

Completion requires the target dependency boundaries, focused host/library/core
and consumer evidence, updated documentation, and explicit disposition of any
remaining contract gaps. Passing extraction checks alone is not full design
conformance. Report what actually ran and what remains blocked or deferred.

Full-suite or expensive validation requires approval; CI gates remain applicable
and must not be implied to have run locally. No new dependencies, sibling widget
edits, hardware qualification or physical operation are authorized by this plan.
This planning work ran no builds or tests.
