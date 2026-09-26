# Plan: CLI Commands and Architecture Redesign

Status: updated for the user's latest command decisions on 2026-09-26; ready for review. Planning only. No production source, repository documentation, tests, processes or hardware changed. The untitled draft was read before this update and was empty; this session document is the updated plan.

## Approved direction
Keep the CLI architectural cleanup, but supersede the earlier requirements to preserve startup syntax, platform asymmetry and MAC enrollment. The user explicitly requests cross-platform devices list/get, selection by Bluetooth ID or name, simulator launch or attach, and removal of expected-MAC verification.

The final simulator decision supersedes the interim --endpoint, --path and --simulator-path proposals: use one required --simulator value, interpreted as an executable path or a local simulator endpoint.

The CLI remains a direct foreground consumer, independent of Web and Aspire. Runtime retains shared safety and machine-lock policy. Include only the provider changes necessary for the approved cross-platform connection and identity behavior; completing the Stock motion protocol or shared Runtime safety features remains separate work.

## Command contract
| Command | Inputs | Behavior |
| --- | --- | --- |
| devices list | None | List discoverable Stock devices with platform ID and name on Windows 11 and macOS 26+. |
| devices get <id> | Required positional platform ID | Read and print that device's details on both platforms. Never show a selector or accept a name in place of the ID. |
| prompt --bluetooth <id-or-name> | Required nonempty value | Resolve a Stock device by platform ID or an exact unambiguous name, connect, and enter the interactive prompt. Name resolution must use the same discovery implementation as devices list. |
| prompt --simulator <path> | Existing simulator executable path | Launch a CLI-owned simulator on an allocated IPv4 loopback port, wait for readiness, connect and enter the interactive prompt. |
| prompt --simulator <local-endpoint> | Supported local simulator endpoint | Connect to an already-running simulator and enter the interactive prompt. Never launch or terminate the external process. |

- `devices` replaces `device`; `devices get <id>` replaces the optional --peripheral-id option and selection flow.
- Exactly one prompt mode is required: --bluetooth or --simulator. Reject missing/empty values, both modes, unexpected arguments and obsolete startup flags before device access.
- Remove prompt --peripheral-id and --wifi-mac. Do not introduce --endpoint, --path or --simulator-path, or keep the old no-value simulator flag.
- Preserve existing help/version support and the in-prompt help/status/set/stop/acknowledge/quit grammar. Do not imply currently unsupported Stock motion operations or acknowledgement become implemented by changing connection options.
- No interactive selector for devices get, Bluetooth ambiguity or simulator resolution. Missing/ambiguous selection fails with useful diagnostics; ambiguity may list candidate IDs and names for an explicit retry.
- Keep result/help output on stdout and diagnostics on stderr. Sanitize remote device display strings using the existing presentation conventions.

## Selection and identity
Use StockBleDiscovery.ScanAsync as the single Stock discovery implementation for list, name lookup and any ID lookup needed by the native platform. Do not copy the scan into PromptHandler or StockIdentityReader.

Resolve an exact platform ID first; otherwise require one exact device-name match from the same bounded discovered set. Match IDs with the existing platform comparison; match names exactly, without fuzzy, prefix, signal-strength or first-result selection. Deduplicate by ID as discovery already does. Zero matches or duplicate names produce errors and no connection. Once resolved, freeze the selected ID for the session; no reconnect fallback to another device.

For devices get, require the positional ID and restrict resolution/connection to that ID. Print current identity details already exposed by the implementation: peripheral ID, name when available, firmware version, chip, Wi-Fi connection status, reported Wi-Fi MAC and sketch MD5. Read-only details must not issue motion writes. Do not fabricate unavailable fields or introduce a new machine-output schema.

Remove separately enrolled/expected Wi-Fi MAC as a prerequisite. Remove ExpectedWifiMac from StockBleOptions, the StockOssmDevice connection plumbing and StockFirmwareProfile.Verify, updating their repository callers and tests. Do not replace the removed flag with mandatory saved enrollment, configuration, a registry or another identity prompt.

Retain live device checks unrelated to expected-MAC matching: selected peripheral consistency, required Stock GATT services/characteristics, bounded/valid identity and pattern payloads, supported firmware profile and required capabilities. Keep the reported Wi-Fi MAC as device information, not authorization or attestation. Update requirements that currently demand an enrolled pair; retain their requirement IDs and unrelated safety constraints.

## Simulator interpretation and ownership
The current adapter and bridge support tcp://127.0.0.1:<port>, with port 1-65535. Use that explicit local endpoint form; remote hosts, unsupported schemes, credentials, extra URL paths and malformed ports must not be silently reinterpreted or ignored. Broader IPv6/hostname/remote support is not implied by this plan.

Use System.CommandLine validation, System.Uri and platform path APIs. A native filesystem path, including a Windows drive path, must not be mistaken for a URI scheme. Resolve filesystem paths normally, require an existing executable and pass arguments with ProcessStartInfo.ArgumentList, never shell evaluation. The help must distinguish path launch from endpoint attach.

- Path: start exactly one owned child on an allocated loopback endpoint; await the existing `ready` protocol; connect only after readiness.
- Endpoint: attach only. If unavailable or not a compatible simulator, fail; never search for or launch a binary as fallback.
- Quit, EOF and cancellation still clean up the device connection. Only the path branch may terminate a process, and only its own child.
- Owned-child order remains device cleanup, child cleanup, lease release last. The attach branch disconnects after its device cleanup but leaves the external process alive.
- Never treat cancellation or timeout as proof of settled I/O or completed process exit. Preserve DeviceOwnershipLock.ReleaseAfterAsync's fail-closed behavior.

## Architecture to retain and improve
1. Program is the one composition root: HostApplicationBuilder, DI, Options, Configuration, standard logging, native registrations, streams and command invocation. No nested BuildServiceProvider. Help/version/parse errors must not open BLE, acquire ownership or spawn children.
2. CliRootCommand/DeviceCommand assemble the renamed tree. PromptCommand exclusively owns startup grammar and forwards a validated Bluetooth, simulator-launch or simulator-attach selection; avoid combinations of nullable values and boolean mode flags.
3. PromptHandler becomes a plain REPL rather than a Command with duplicate startup options. It does not create devices/processes or acquire/release the machine lease. Retain current motion mapping and limitations until the separate Runtime/Stock work changes them; do not duplicate safety policy in a new CLI controller.
4. Extract one internal PromptSession for foreground resource lifetime, readiness, subscriptions and observations. Keep reported state separate from submitted intent. It owns the selected provider and optional child, using Runtime's existing ownership gate rather than inventing a second queue or interruption latch.
5. Centralize native BLE DI for Windows and macOS. Inject StockIdentityReader instead of letting it create a second container; retain explicit ownership of the macOS dispatch queue. List, get and prompt all use the shared lock before BLE access. Get retains ownership through read/disconnect cleanup; prompt retains it for the session. Do not nest another acquisition while resolving a name under the same session lease.
6. SimulatorBackgroundService remains the CLI-owned process boundary. Endpoint attachment never constructs it. Avoid combining explicit provider startup with AddOssmSimulator's IHostedService registration in a way that creates competing owners or starts the device before the lease.
7. Intentionally connect command cancellation and host shutdown to one awaited session cleanup path. Preserve ordinary failure over cancellation when mapping exit codes. Runtime's fail-fast path is not an ordinary catch-and-return failure and must not be weakened.

No new projects or dependencies are required by this design. Add internal types only where they represent the concrete selection/lifetime responsibilities above; no plugin registry, general command framework or parallel logging system.

## Implementation phases and dependencies
### A. Update contracts and write focused failing tests
1. At the start of implementation, revise the existing CLI/adapter requirements and migration record for the requested command names, cross-platform behavior, no-selector get, simulator launch/attach distinction and removal of enrollment. Retain DC/CC/PST IDs. Update AGENTS' old simulator invocation wording where necessary, without weakening child ownership or hardware restrictions. This is proposed work, not a claim that contracts or code have already changed.
2. Extend the existing CLI parser and behavior tests for the new grammar and mode validation. Retain existing resource/feedback/cleanup regressions. Replace old enrollment expectations with tests proving selection without configured MAC while unsupported firmware, wrong peripheral, malformed payloads and missing characteristics still fail. Prove new behavior/bug tests fail for the intended reason before implementation.
   - Depends on the decisions recorded above. Test-first changes remain incremental; do not invent an entire new harness before testing one slice.

### B. Shared discovery, details and identity
3. Wire StockBleDiscovery and injected BLE services on Windows and macOS; implement required positional devices get and remove its selector. Share bounded discovery/name resolution with the Bluetooth prompt, and preserve no-motion behavior of read-only commands.
4. Remove expected-MAC matching from the Stock option/profile/device path and affected callers. Keep all other connection/profile validation. Bind the new --bluetooth selection to the resolved peripheral ID without introducing saved enrollment.
   - Steps 3 and 4 can be developed in parallel after agreeing the connection input; one owner integrates Program/StockIdentityReader registrations. Cross-platform native compilation and focused deterministic BLE tests accompany each change.

### C. Prompt composition and simulator modes
5. Remove duplicate startup parsing, extract PromptSession, and make the REPL consume the selected session. Unify root composition and cancellation handling without adding background command machinery or Web/Aspire dependencies.
6. Implement --simulator path-versus-endpoint validation and dispatch. Reuse the current bridge readiness protocol and allocated loopback port for path launches; bypass child construction entirely for endpoint attachment. Test startup failure, child exit and cancellation races, and fix any demonstrated orphaning or duplicate-disposal behavior at its process owner.
   - Step 5 depends on the agreed selection contract; simulator parsing/process work can proceed in parallel with native BLE work, but session wiring depends on step 5. Do not edit shared Program/PromptCommand files concurrently without a designated owner.

### D. Lifetime verification and documentation
7. Retain/fix CLI-local lifecycle guarantees: provider disposal is attempted after failed preliminary Stop; child cleanup is attempted where owned; failures remain observable; unresolved cleanup never releases the lease to a new writer. Preserve existing Stop semantics and Runtime policy rather than claiming the pending shared safety work is finished.
8. Extend the existing integration surface to prove actual CLI invocation, stdin/EOF/cancellation and process ownership for launch and attach. Exercise both native builds without operating hardware; use deterministic BLE boundaries for list/get/resolve/connect behavior. Update existing README/help/migration status for the final commands and actual verified limitations.
   - Depends on the affected implementation slices. Windows and macOS checks can run independently. Full solution or expensive runs still require approval; report unavailable environments and pending evidence.

## Critical existing files
Expected CLI changes:
- d:/src/ossm-web-controls/src/OssmSharp.Cli/Program.cs
- d:/src/ossm-web-controls/src/OssmSharp.Cli/Commands/CliRootCommand.cs
- d:/src/ossm-web-controls/src/OssmSharp.Cli/Commands/DeviceCommand.cs
- d:/src/ossm-web-controls/src/OssmSharp.Cli/Commands/DeviceListCommand.cs
- d:/src/ossm-web-controls/src/OssmSharp.Cli/Commands/DeviceGetCommand.cs
- d:/src/ossm-web-controls/src/OssmSharp.Cli/Commands/StockIdentityReader.cs
- d:/src/ossm-web-controls/src/OssmSharp.Cli/Commands/PromptCommand.cs
- d:/src/ossm-web-controls/src/OssmSharp.Cli/Prompt/PromptHandler.cs
- d:/src/ossm-web-controls/src/OssmSharp.Cli/Simulator/SimulatorBackgroundService.cs
- d:/src/ossm-web-controls/src/OssmSharp.Cli/OssmSharp.Cli.csproj: native targeting/registrations only as needed, not a new project or dependency.
Provider changes/reuse:
- d:/src/ossm-web-controls/src/OssmSharp.Device.Stock/StockBleDiscovery.cs: shared scan implementation.
- d:/src/ossm-web-controls/src/OssmSharp.Device.Stock/StockBleOptions.cs: remove expected-MAC requirement.
- d:/src/ossm-web-controls/src/OssmSharp.Device.Stock/StockFirmwareProfile.cs: remove enrolled-MAC comparison, retain firmware/GATT/payload checks.
- d:/src/ossm-web-controls/src/OssmSharp.Device.Stock/StockOssmDevice.cs: constructor/connection plumbing for the revised identity contract and supported native composition.
- d:/src/ossm-web-controls/src/OssmSharp.Device.Simulator/Models/SimulatedDeviceOptions.cs: reuse endpoint validation, do not bypass it with unvalidated Options.Create.
- d:/src/ossm-web-controls/src/OssmSharp.Device.Runtime/DeviceOwnershipLock.cs and DeviceBase.cs: reference authoritative lifetime behavior; no new shared safety implementation.
- d:/src/ossm-web-controls/tools/ossm-sim-bridge/src/main.rs: reuse existing --listen/ready protocol; no Rust or sibling changes currently required.
Tests/docs:
- d:/src/ossm-web-controls/tests/OssmSharp.Cli.Tests/DeviceCommandTests.cs
- d:/src/ossm-web-controls/tests/OssmSharp.Cli.Tests/SimulatorPromptTests.cs
- d:/src/ossm-web-controls/tests/OssmSharp.Cli.IntegrationTests/SimulatorPromptIntegrationTests.cs
- Existing Stock profile/options/connection tests associated with changed symbols; reuse their current files and helpers.
- d:/src/ossm-web-controls/AGENTS.md
- d:/src/ossm-web-controls/README.md
- d:/src/ossm-web-controls/.docs/specs/README.md
- d:/src/ossm-web-controls/.docs/specs/web-control-design.md
- d:/src/ossm-web-controls/.docs/specs/device-control/spec.md
- d:/src/ossm-web-controls/.docs/specs/device-control/adapters.md
- d:/src/ossm-web-controls/.docs/plans/direct-device-migration.md
New internal types only as needed: d:/src/ossm-web-controls/src/OssmSharp.Cli/Prompt/PromptSession.cs and Models/PromptSelection.cs. No project restructuring means no project-architecture diagram redraw.

## Acceptance and verification
- `devices list` and `devices get <id>` work through native Windows/macOS builds using the same underlying discovery/details behavior. Get never reads a selector response and accepts no missing ID.
- --bluetooth accepts an ID or uniquely matching name. Tests prove reuse of discovery, exact forwarding of the resolved ID, no-match/duplicate-name failures, ID precedence and no device access on invalid input. No name-to-first-device fallback or motion on connect.
- No configured Wi-Fi MAC is required. Tests still reject wrong connected peripheral, unsupported firmware, missing GATT surface and malformed/oversized identity/pattern payloads. Reported MAC remains data.
- --simulator accepts either an executable path or the documented loopback TCP endpoint. Tests cover Windows/Unix path handling, whitespace/quoting, missing executable, invalid/nonlocal endpoint, missing mode/value, mutually exclusive modes and rejected obsolete flags.
- Owned-child tests prove startup readiness, Stop/disposal-before-child-cleanup and lease-last ordering. Attach tests prove no process launch/termination and that the externally started simulator survives CLI exit. Connection/startup failures never cause implicit launch/attach fallback.
- Preserve early feedback, invalid REPL input with no writes, EOF/cancellation cleanup, lease retention on cleanup failure and ordinary failure not being overwritten by exit 130. Keep command results separate from host/diagnostic output.
- Use existing .NET 10, xUnit v3/MTP v2, FastMoq, AwesomeAssertions, deterministic time/BLE fakes and current simulator fixtures. Load approved runner guidance before forming executable commands. No extra dependencies, skipped failures or test-framework migrations.
- Run focused tests and native compilation; real simulator integration owns process/connection proof. Inspect direct/transitive dependencies for forbidden Web/Aspire coupling. Do not use source scans to substitute for argument or behavior assertions.
- Verify Windows and macOS separately; this Windows planning session does not prove macOS runtime behavior. No tests or builds have been run for this plan update.

## Scope limits and honest completion
Requested command changes, native cross-platform list/get/Bluetooth connection composition, simulator attach/launch and MAC-verification removal supersede the earlier CLI-only refactor restrictions where needed. Other restrictions remain: no new projects/dependencies, web redesign, accounts/database/enrollment storage, remote simulator hosting, firmware changes, physical operation, branches, commits or pushes.

Preserve current in-prompt operation support until its owning Runtime/Stock work completes. Do not claim a complete Stock motion implementation, interrupt latch/revision, Stop-priority behavior, single overall shutdown budget, full OS-signal coverage, selected-provider packaging isolation or physical qualification from this change. The existing Runtime fail-fast-versus-exit-1 discrepancy and stale-motion Stop semantics remain explicitly separate rather than silently changed.
