# Repository instructions

## Stack

- You must use .NET 10, C#, and **ASP.NET Core Blazor Interactive Server + minimal APIs**.
- You must use **xUnit v3 + MTP v2**, not MSTest, xUnit v2 or VSTest.
- You must use **Aspire AppHost** only for optional local development and test
  orchestration. Executables must build, start and operate without AppHost or Aspire.
- When using AppHost locally, use `aspire run`; do not add a custom orchestrator.
- Logging, metrics, tracing and health must work through standard .NET hosting,
  `ILogger<T>` and OpenTelemetry without Aspire or its dashboard. `OssmSharp.Defaults`
  owns host-neutral configuration; `OssmSharp.Web.ServiceDefaults` adds web-only
  hosting. Startup must not require a telemetry collector.
- You should prefer native .NET best practices and remove redundant boilerplate.
- You must not use Node.js tooling or scripts.
- You may use Python with Playwright for browser regression checks and private
  widget CSP hash calculation. You must keep application builds and orchestration
  native to .NET/Aspire, manage Python with `uv`, and not use `python -m pip install`.
- You must update `.docs/diagrams/architecture.html` when changing project
  structure. Each folder under `src` should typically have one box in the diagram;
  pure libraries may have none.
- You must update `.docs/specs` at the start of making durable changes to
  requirements. You must double-check before proceeding that the user's intent
  is honoured and the change does not mask or enshrine the wrong behaviour.
- You must implement with cross-platform support for Windows 11 and macOS 26+.


## Coding

- You must use `OssmSharp.*` for owned namespaces, projects, assemblies and
  packages, following the [accepted project map](.docs/plans/direct-device-migration.md#project-and-identity-map).
  Do not rename external packages, firmware identifiers or upstream Rust projects.
- `OssmSharp.Cli` must be a foreground interactive consumer of
  `OssmSharp.Device.Primitives`, `OssmSharp.Device.Runtime` and the selected
  provider. It must not depend on any `OssmSharp.Web.*` project, ASP.NET, UI or
  Aspire, or require web credentials, sessions or application policy.
- `OssmSharp.Web.Api` must compose one `OssmSharp.Web.Application` authority,
  controller and selected device provider in one lifetime. Only AdminWeb and
  GuestWeb use `OssmSharp.Web.Client` to call Web.Api; Web.Api must not depend on
  UI components. Web.Application must not depend on concrete providers.
- `OssmSharp.Device.Runtime` must own shared device execution, Stop,
  interruption and machine-wide ownership mechanics without web policy or
  provider dependencies. `OssmSharp.Device.Primitives` must remain independent
  of hosting, defaults and providers; Simulator must not depend on Stock.
- With `prompt --simulator <path>`, the CLI starts and owns the specified
  simulator process for its foreground session, stopping it after device cleanup.
  Web.Api connects to an independently started simulator; the simulator adapter
  never launches or terminates it. AppHost may start the web simulator for local
  development but must never own BLE.
- You must keep AdminWeb, GuestWeb and Web.Api as separate processes. Support
  CLI-only operation and Web.Api without UI hosts or AppHost. Web.Api and its
  device lifetime are intentionally coupled; do not claim an independent device
  service. Do not present a target diagram as completed implementation.

## User interface conventions

- You must keep equivalent widgets consistent across operator and guest pages:
  reuse shared components, slot markup, typography, spacing, sizing, semantic
  variants and accessible behavior. Compare counterpart widgets before adding
  local markup or styles; keep differences only when their purpose is clear.
- Operator and guest pages must share `ControlLayout`, control sizing, spacing
  and notification behavior. You must not create parallel page shells or
  duplicate guest headings.
- You must put connection, reconnection, operation and error notices at the top
  right, outside normal layout flow. Connection failures must take precedence
  over other notices. You must keep framework-managed connection UI outside
  interactive route DOM.
- On guest connection loss, you must show
  `Connection lost. Open the invitation link again.` as a standard danger
  `RwAlert` above live feedback, grayscale the remaining guest interface and
  hide the paused status message.
- You must make shared widget styling changes in **RetroWebUi**, then consume
  the reviewed private package version here. You must update its package
  checksum and verify CSP style hashes together. You must not patch copied
  assets or introduce app-specific versions of shared widget states.
- You must use named semantic styles: waiting must be warning/gold, connected
  and successful actions must be success/green, and **Stop and end session** must
  be warning/yellow. Disabled buttons must be grayscale. **Allow control** must
  be green when actionable and grayscale when inactive, without an activation
  glow; it must be enabled whenever it is safe for the operator to press,
  including after guest connection changes. Preserve device-readiness and safety
  checks.
- Every button must have an accessible name and a useful tooltip. Icon-only
  buttons must have an explicit accessible label; you must not use an HTML `alt`
  attribute on buttons. Adjacent text inputs and buttons must have matching
  heights.
- You must put a limits widget's dirty status at its top right. You must keep
  applied values marked on sliders while drafts differ and preserve unsent edits
  during status refresh. You must not duplicate applied values in a separate
  "Active" row.
- You must keep **Copied** text and temporary success styling on the same timer.
- Invitations must be automatic, reusable and non-expiring. **Cycle** and operator
  **Stop and end session** must revoke guest authority and replace the invitation;
  guest leave must not replace it.
- Applying compatible limits must not pause or rearm control. You must reject
  conflicting limits atomically with an actionable message. A stale-policy retry
  may retain explicitly sent settings, never dispatch unsent slider edits or extend a
  previously accepted motion lease through status refresh alone.
- Startup, reconnect and refreshed invitations must never arm or resume motion.

## Authority and safety

- Web.Api must exclusively own web sessions, policy, timers, the single writer
  and device connection; its backend must operate with both UI hosts absent.
  The CLI is an alternative direct owner, not a web service client. Both owners
  must acquire the same machine-wide exclusive lock before device access and
  retain it through issued I/O and cleanup; permission failure must not fall
  back to a per-user lock.
- Only Web.Api must expose the existing SignalR endpoint `/hubs/control/v1`.
  Web.Application must enforce guest authority before device execution; neither
  UI host may bypass Web.Api's writer.
- AdminWeb must remain unauthenticated and bound to `127.0.0.1`; you must retain
  Host/Origin protection and must not expose/proxy it remotely. It must authenticate
  server-to-server with its generated service key.
- AdminWeb listener configuration must not add/replace endpoints through
  Kestrel, hosting URL precedence or configuration reload; use
  `OSSM:Admin:AdminPort`.
- Web.Api must authenticate each web host and enforce operator/guest role
  separation. Web.Application must independently enforce guest invitation/session
  capabilities and expiry; host authentication must not weaken guest authorization
  or arm motion. The direct CLI requires no web credentials.
- GuestWeb must expose only Join/input/Stop/leave/scoped status, with no
  operator/device access. Browser and Blazor state must never authorize motion.
- Web.Api must support an explicitly configured remote HTTPS/WSS endpoint;
  you must not bypass certificate validation, expose service credentials to browsers,
  trust forwarded headers, or introduce a proxy by default.
- Browser and Blazor state must be treated as untrusted; neither may authorize motion.
- You must use one-box, accountless capabilities and one-click invitations;
  you must not add accounts, a database or cloud services.
- For the Simulator provider, you must reuse `ossm-rs`'s `ossm-motion` through
  the independently started simulator, without making the adapter its process
  owner. This does not require the Stock provider or CLI to use a simulator.
- You must not operate physical hardware or perform physical qualification
  without separate explicit authorization. This restriction does not prohibit
  implementing device-provider software or testing it with deterministic BLE
  fakes. You must not edit sibling repositories or flash firmware.
- Stop and interruption must override pending input. Runtime must own the only
  interruption latch and revision; Web.Application may project but not independently
  clear it. Only the local admin may acknowledge a detected physical override,
  using an explicit CLI command in REPL mode or the operator action in web mode.
  Acknowledgement must verify the current revision and stopped baseline and leave
  control **paused**, never restore the prior motion tuple or resume motion.
- Startup/reconnect must never arm/resume. You must not treat cancellation as
  proof that device I/O stopped.
- You must preserve bounded work, execution ownership and safety checks when simplifying.
- You must read `.docs/specs/README.md` and affected device-control requirements;
  you must retain their IDs.

## Performance

- You must use `Span`/`ReadOnlySpan` synchronously and `Memory`/`ReadOnlyMemory`
  for async I/O.
- You should avoid needless copies, encoding round trips and hot-path allocations.
- You must bound frames, clear pooled secrets, and return buffers only after all
  consumers finish.
- You must not use spans across `await`; you should prefer correctness over
  speculative optimization.

## Tests

- You must write tests before behavior and prove regressions fail before fixing.
- Tests must use FastMoq when the test class can extend `MockerTestBase<T>`
  where `T` is the SUT. Use InternalsVisibleTo if required. Model/record types
  are typically excluded.
- Test assertions must use AwesomeAssertions lib, when straightforward to do.
- You must use `System.TimeProvider` for clock fakes.
- For browser-affecting changes, you should prefer the fastest applicable focused
  browser check before broader validation. You should use browser checks for
  high-value, user-visible integration behavior and prove lower-level details
  with component or unit tests.
- You should use these MTP v2 commands:
  ```powershell
  dotnet test OssmSharp.slnx
  dotnet test --project tests/OssmSharp.Web.Application.Tests/OssmSharp.Web.Application.Tests.csproj
  dotnet test --project tests/OssmSharp.Web.Application.Tests/OssmSharp.Web.Application.Tests.csproj --filter-method '*MethodName'
  dotnet test --project tests/OssmSharp.Web.Application.Tests/OssmSharp.Web.Application.Tests.csproj --list-tests
  ```
- You must update these paths when the solution and test projects are renamed.
- You must never remove failing tests or claim unrun checks passed witout justification.
- Before claiming the direct-device migration is implemented, verify the
  [acceptance matrix](.docs/plans/direct-device-migration.md#acceptance-matrix).
  Record software conformance, live device observations and physical
  qualification separately; simulator results do not prove Stock behavior.
- You must separate test projects into unit tests and integration tests, with
  shared test helpers. Integration test projects must be named with the assembly
  they're testing followed by ".IntegrationTests". Unit test projects the same, but
  ending with ".Tests".
- In Playwright, you should prefer `get_by_role`, `get_by_label`, and stable
  `data-testid` locators; you should avoid positional, incidental class, and
  DOM-structure selectors.
- You must use web-first assertions for observable outcomes, not fixed delays or
  polling; you must isolate browser state and control clocks/external boundaries
  when needed.
- You must assert markup, CSS classes, and pixel geometry only when they are an
  explicit component or visual contract.
- You must not write tests that predominantly validate behaviour in dependencies,
  including Aspire AppHosts.

## Boundaries

- You must use only approved dependencies that are explicit, pinned, and necessary.
- You must use compatible installed tools and record their actual versions.
- You must obtain explicit approval before branches, commits, pushes, schemas
  and other-checkout changes.
- You must not put secrets in source. You must use Podman, not Docker, when
  containers are authorized.
- You must not reference absolute paths.
