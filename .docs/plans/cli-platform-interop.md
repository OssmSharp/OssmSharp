# CLI device status and control with composable discovery

## Status and scope

Implementation authorized on 27 September 2026. The shared user instructions
have already been updated. This document replaces the earlier CLI-only
extraction proposal and is the authoritative plan for this work.

Implementation checkpoint: discovery/information DI, opaque identifiers,
Interop composition and the guarded status/initialization/Stop path are present.
Stock now implements qualified software dispatch, with mandatory Runtime
authority revisions and guards. Independent focused checks passed 78 Runtime,
101 native macOS Stock, 98 CLI unit, two real native CLI composition and one
portable process case, with zero failures or skips. Adversarial findings were
addressed, with the final Stop/baseline corrections source-checked and independently
tested by the coordinating agent. Windows-native and queue failure-path
verification remain blocked.
Production composition has no qualified mapping, and the physical
qualification allowlist remains empty. The
[migration evidence](direct-device-migration.md#m4-interactive-direct-device-cli-and-first-real-control-command)
records focused software results and unrun platform/resource-lifecycle checks.

The resumed software increment separates missing implementation from physical
qualification. Implement full Stock dispatch through an internal, injected,
connection-bound qualification/mapping boundary. Production composition retains
no qualified mapping; controlled test composition substitutes that boundary and
BLE only, exercising the real CLI parser, Runtime, Stock validation and encoder.
This is not a runtime opt-in flag or a populated physical allowlist.

Runtime exposes its existing authority generation as a read-only revision.
Submissions capture that revision and a mandatory synchronous execution guard;
there is no revision-free or unconditional-guard overload. Pending work retains
the tuple, revision, guard and revocation source together. Stop and other
authority-invalidating events revoke in-flight work immediately. Initialization
and acknowledgement check the captured authority revision without restoring a
previous tuple.

Validate the complete requested transition before queuing or issuing any write,
using a Runtime-owned optional validation capability that Stock always
implements. Revalidate at dispatch. Resolve Stock qualification from verified
connected identity/profile data, clear it on disconnect, and pass only an exact
validated tuple to motion encoding; reject unsupported rounding or clamping.
Missing qualification rejects nonzero output and envelope edits before any
write. Explicit Stop and initialization's speed-only zero remain available.
Each issued command retains settlement ownership and checks revocation before
any subsequent command. Native change-only feedback still bounds active output:
without a fresh report, the existing expiry must fault and request Stop rather
than invent a heartbeat.

The goal is the CLI path: discover, select, connect, display reported status,
submit pattern/speed/stroke/depth, Stop, and exit with owned cleanup. Generic
contracts and Interop organization support that outcome; they are not proof
that native device control works.

Work stays in the existing Primitives, Runtime, Stock and CLI projects, their
tests and directly related documentation. No new project, dependency, firmware,
discovery mechanism, registry or process is planned. No physical operation,
branch, commit or push is authorized by implementation approval.

The [direct-device migration plan](direct-device-migration.md) owns broader
migration sequencing. The [device requirements](../specs/device-control/spec.md)
and [CLI design](../specs/web-control-design.md) retain their DC and CC IDs.
Preserve the current baseline's deliberate removal of device locking; do not
restore superseded concurrent work as part of this change.

## Contracts and composition

- `IDeviceDiscovery.ScanAsync(CancellationToken)` returns
  `IReadOnlyList<DeviceIdentifier>`.
- `DeviceIdentifier` is an abstract record with public get-only `Id` and `Name`.
  Stock supplies an internal implementation with any private native state.
  There is no public object-valued context, property bag, or separate
  `DiscoveredDevice`/`IDeviceContext` pair.
- Consumers select through public metadata and pass the original identifier
  unchanged. They must not cast, reconstruct, serialize, or inspect its
  provider internals. IDs are provider-defined locators, not universally
  Bluetooth addresses. Record equality does not establish ownership.
- `IDeviceProvider.ReadInformationAsync(DeviceIdentifier, CancellationToken)`
  returns a separate `DeviceIdentityDetails` record: the selected identifier,
  Wi-Fi MAC, chip, Wi-Fi connection state, sketch MD5 and firmware version.
  Providers may report documented absence for unavailable descriptive fields;
  Stock must retain its required-field validation.
- Keep the existing connected-profile `DeviceInformation` contract unchanged.
  Identity-only reads must not fabricate supported patterns or a verified
  connected profile to reuse that type.
- Discovery/provider interfaces belong in Primitives and the result records in
  its Models namespace. Stock owns implementations and protocol interpretation;
  the CLI owns formatting and sanitization.
- Use a registered `Func<DeviceIdentifier, IOssmDevice>` for selected-device
  creation. Remove the native constructor that creates its own transport.
  DI is mandatory; no compatibility constructor bypass is retained.
- Keep private Stock connection/transport seams provider-owned. Rename them
  `IDeviceConnection` and `IDeviceTransport`; native protocol types do not become
  universal abstractions merely because interface names are generic.

Identifiers describe selection, not connection, authorization or readiness.
They are composition-bound and not persisted across provider teardown.
Discovery creates no caller-owned connected resource. The identifier therefore
needs no disposal contract; sessions/operations own connections, and native
infrastructure retains its own explicit owner. Stock validates identifier
compatibility and selected identity before I/O. Foreign identifiers fail
explicitly rather than selecting another device.

```text
list -> discovery -> identifiers -> public metadata
get -> discovery -> selected identifier -> information provider -> typed details
    -> CLI formatting
prompt -> discovery -> selected identifier -> device factory -> IOssmDevice
       -> shared execution, status, Stop and cleanup
```

List never connects. Get preserves exact-ID selection and existing detail
labels, order, optional-name handling and sanitization. Prompt preserves
ID precedence and exact unambiguous name selection. Simulator paths never
invoke Bluetooth discovery.

## Platform ownership

Keep Interop as a folder/namespace in the CLI, not a new project. Place common,
macOS and Windows `IServiceCollection` extensions there. Common
`AddCliPlatformServices` returns the caller-owned macOS dispatch queue lifetime,
or null for Windows/portable builds.

```csharp
using var platformResources = builder.Services.AddCliPlatformServices();
using var host = builder.Build();
```

The host is disposed before the queue. Registration failure releases acquired
resources before rethrowing, with exactly one owner on every transfer path.
Do not register an external queue expecting DI to dispose it. Retain the existing
local preview-diagnostic exemption without adding suppressions.

Stock owns native registrations and a shared deferred BLE manager. Registration,
command construction, help/version and simulator paths must not initialize BLE.
Service-provider access belongs only in composition factories. Do not build a
second container or inject a service locator.

Portable unavailable implementations retain explicit error messages and exits;
never return empty discovery or fabricated data as a fallback. Move the existing
executable predicate to Interop without changing path/URI validation order,
Windows extension checks or Unix execute-bit behavior.

## Ordered implementation

1. **Baseline and owner contracts.** Read current code and requirements, establish
   focused tests and update adapter composition guidance. Add regression tests
   before new behavior, demonstrating the missing native dispatch rather than
   merely deleting the existing read-only rejection assertions.
2. **Discovery through reported status.** Implement identifiers, instance
   discovery, identity information and injected native transport/device
   construction. Wire CLI selection and prove exact identifier propagation,
   persistent connection, actual feedback and cleanup with native BLE fakes.
3. **Native commands with shared execution guarantees.** Implement the missing
   provider dispatch and required Runtime readiness, one writer, bounded pending
   work, Stop invalidation, interruption revision and acknowledgement into paused
   state. CLI parsing must not become a second execution engine. Review this
   execution design before enabling native commands.
4. **Finish Interop extraction.** Consolidate OS directives/native types,
   per-platform registration and executable checks. Remove superseded readers,
   static discovery and constructor-created service dependencies. Preserve
   simulator behavior without expanding simulator features.
5. **Integrate and verify.** Run affected builds and focused tests, review changes
   adversarially, fix supported findings, and update README and migration evidence
   with actual outcomes and pending platform checks.

Native control is an intentional behavior addition. At the baseline,
`StockOssmDevice.DispatchMotionAsync` throws `NotSupportedException` and the
prompt allows motion only for simulators. Do not remove that gate before the
native path supports required execution guarantees.

Keep existing named `set` options and percentage validation, supported-pattern
checks, sensation 50 and buffer 0 defaults. Do not add one-shot commands or more
configuration without a requirement. Submitted values and completed writes are
not reported device state or physical-motion evidence.

Native prompt initialization is an explicit `initialize` command, not an
implicit effect of discovery or connection. It authorizes zero and knob-limit
enable/readback, then requires fresh stopped play-state evidence before motion.
There is no CLI navigation/homing/calibration; the operator uses the physical
controls. The prompt must remain responsive to status/Stop/quit while motion
work is owned by Runtime. A missing change-only notification or expired
feedback deadline must inhibit motion, not trigger a cached read or replay.

## Acceptance and verification

- Use xUnit v3/MTP v2, FastMoq, AwesomeAssertions and existing helpers. Keep
  behavior tests at their owner and composition tests at the wiring boundary.
  Run focused checks; no full solution suite is authorized.
- Exercise generic consumers using different private fake identifier
  implementations. Assert exact instance/token propagation, not descriptor
  scans or generic naming alone.
- Preserve native scan service filters, nonblank IDs, case-insensitive
  deduplication, order, 64-result cap and five-second window, including early
  source completion. Test permission denial and subscription cleanup with
  controlled observables, not arbitrary sleeps.
- Preserve known-peripheral/fallback selection, selected-ID verification,
  identity read bounds, 30-second operation budget, separate five-second
  disconnect budget and visible unsettled-cleanup failures.
- Verify typed information fields and exact CLI output separately. Get scans
  once for selection; no second scan merely to recreate an identifier.
- Verify lazy shared manager, DI replacement, no BLE on help/version/simulator,
  caller-owned queue lifetime and failure cleanup.
- Verify status represents received feedback, not identity-only output or
  submitted settings. Show connection/readiness/fault distinctions.
- Exercise the real parser/runtime/provider with deterministic native boundaries.
  Verify exact pattern/speed/stroke/depth/defaults, write ordering, no writes for
  rejected requests, Stop precedence, pending-input invalidation, cancellation
  versus settled I/O, startup/reconnect inhibition and owned exit cleanup.
- Verify interruption is latched and only explicit local acknowledgement of the
  current revision and stopped baseline enters paused state; never replay motion.
- Compile Primitives without provider/hosting dependencies. Run affected
  portable and native macOS builds/tests. Native Windows evidence requires a
  Windows host and must be reported pending if unavailable.
- Check no CLI OS macros/native dependencies remain outside Interop, while using
  runtime tests for behavior rather than treating source scans as proof.
- Report discovered/passed/failed/skipped/not-run counts, identifying unknown
  counts. Separate software conformance, live observations and physical
  qualification; no hardware operation is authorized.

Toolchain previously observed: .NET 10.0.401, macOS workload
26.5.10318/10.0.100, Xcode 27.0 (27A266a). Use per-command developer-directory
selection for native macOS builds; do not change machine-wide settings or bypass
SDK checks. Restore only after a dependency change or missing-assets failure.
