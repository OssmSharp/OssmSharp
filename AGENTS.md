# OssmSharp repository instructions

These rules add project constraints to the user and .NET instructions.
Do not duplicate general coding, testing, or review policy here.

## Stack and tooling

- Use .NET 10 and C#. Web UIs use Blazor Interactive Server; HTTP APIs use
  minimal APIs. Support Windows 11 and macOS 26+.
- Use xUnit v3 with MTP v2, not MSTest, xUnit v2, or VSTest.
- AppHost is optional local development/test orchestration. Use `aspire run`;
  do not add an orchestrator. Executables must build and operate without Aspire.
- Use standard hosting, `ILogger<T>`, and OpenTelemetry for logging, metrics,
  tracing, and health. `OssmSharp.Defaults` owns host-neutral configuration;
  `OssmSharp.Web.ServiceDefaults` adds web defaults. Neither startup nor
  diagnostics may require a collector or Aspire dashboard.
- Do not use Node.js tooling. Python is allowed for Playwright browser checks
  and private-widget CSP hashes; manage it with `uv`, never `python -m pip`.
  Keep application builds and orchestration native to .NET/Aspire.
- Use approved, explicit, pinned dependencies and compatible installed tools.
  Record tool versions used for verification. Use Podman when containers are
  authorized. Do not put absolute paths or secrets in repository files.

## Ownership and composition

- Use `OssmSharp.*` for owned names, following the
  [project map](.docs/plans/direct-device-migration.md#project-and-identity-map).
  Do not rename external packages, firmware identifiers, or upstream Rust projects.
- `Cli` is a foreground interactive consumer of Device.Primitives, Device.Runtime,
  and the selected provider. It must not depend on Web projects, ASP.NET, UI,
  Aspire, web credentials, sessions, or application policy.
- `Web.Api` composes one Web.Application authority/controller and selected
  provider. It owns web sessions, policy, timers, the writer, and device lifetime,
  and must operate without either UI host. Only AdminWeb and GuestWeb use
  Web.Client. Web.Api must not depend on UI components; Web.Application must not
  depend on concrete providers.
- Keep AdminWeb, GuestWeb, and Web.Api in separate processes. Web.Api and its
  device lifetime are intentionally coupled, not an independent device service.
- Device.Runtime owns shared execution, Stop, and interruption without web
  policy or provider dependencies. Device.Primitives contains host-neutral
  contracts and values, with no hosting/defaults/provider dependencies.
  Simulator must not depend on Stock.
- Keep provider operations cohesive. Discovery and reads may share a provider;
  identity-only and connected information may share a model. Reported identity
  never establishes verified capabilities, readiness, or authority.
  Use the motion-change exception for unexpected speed changes.
- Prefer common sequencing in `DeviceBase` with provider-specific preparation,
  transition-validation, and dispatch hooks. Keep qualification and BLE protocol
  details in Stock. Removing interfaces must not remove required behavior,
  including speed-only Stop independent of stale-geometry qualification.
- Runtime responsibilities do not mandate a separate execution-session object.
  Retain an additional layer only for a distinct role: compare its write,
  feedback, interruption, and disposal state with the base. Prefer one execution
  owner and operation path; keep web authorization in Web.Application.
- For `prompt --simulator <path>`, the CLI owns the child process and stops it
  after device cleanup. For `prompt --simulator tcp://127.0.0.1:<port>`, it must
  neither launch nor terminate the external process.
- The Simulator adapter connects to an independently started simulator using
  `ossm-rs`'s `ossm-motion`; it never owns a process. Web.Api uses that external
  simulator. AppHost may start it for development but must never own BLE.
  Stock and CLI operation must not require the simulator.

## Authority and safety

- Only Web.Api exposes `/hubs/control/v1`. Web.Application must authorize guest
  operations before execution. UI hosts must not bypass the writer; browser and
  Blazor state never authorize motion.
- AdminWeb is unauthenticated locally and bound to `127.0.0.1`, with Host/Origin
  protection. Never expose or proxy it remotely. Authenticate its server calls
  with the generated service key. Configure its listener only through
  `OSSM:Admin:AdminPort`, not Kestrel overrides, hosting URLs, or reloads.
- Web.Api authenticates web hosts and separates operator/guest roles.
  Web.Application also validates invitation/session capabilities and session
  expiry. Host authentication must not weaken guest authorization or arm motion.
- GuestWeb exposes only Join, input, Stop, leave, and scoped status, with no
  operator/device access.
- Support explicitly configured remote HTTPS/WSS for Web.Api. Never bypass
  certificate validation, expose service credentials to browsers, trust
  forwarded headers, or add a proxy by default.
- Keep accountless, one-box capabilities and one-click invitations. Do not add
  accounts, a database, or cloud services.
- Stop and interruption override pending input. Runtime owns the only
  interruption latch/revision; Web.Application may project but not clear it
  independently. Only the local admin may acknowledge a physical override,
  explicitly through the CLI REPL or operator UI. Check the current revision
  and stopped baseline; acknowledgement leaves control paused.
- Startup, reconnect, and refreshed invitations must never arm or resume motion.
  Never restore the previous motion tuple after interruption. Cancellation does
  not prove issued I/O stopped. Preserve bounded work and cleanup ownership.
- Do not operate hardware or perform physical qualification without separate
  explicit authorization. Do not flash firmware or edit sibling repositories
  during device work. Provider implementation and deterministic BLE-fake tests
  are allowed.

## UI behavior

- Compare operator/guest counterparts before editing widgets. Reuse shared
  components, slots, typography, spacing, sizes, semantics, and accessibility.
  Both pages use `ControlLayout`; do not add parallel shells or guest headings.
- Put connection, reconnection, operation, and error notices at the top right,
  outside layout flow. Connection failures take precedence. Keep framework
  connection UI outside the interactive route DOM.
- On guest connection loss, show `Connection lost. Open the invitation link again.`
  in a danger `RwAlert` above live feedback, grayscale the remaining interface,
  and hide paused status.
- Change shared widget styling in RetroWebUi and consume the reviewed private
  package. Update its checksum and verify CSP style hashes together; do not
  patch copied assets or create app-specific shared-widget states.
- Use semantic variants: waiting = warning/gold; connected/success = green;
  **Stop and end session** = warning/yellow; disabled = grayscale.
  **Allow control** is green when actionable, otherwise grayscale, with no
  activation glow. Enable it whenever safe, including after guest connection
  changes; retain readiness and safety checks.
- Give every button an accessible name and useful tooltip. Icon-only buttons
  need explicit labels, never HTML `alt`. Adjacent inputs/buttons match heights.
- Put limits dirty status at the top right. Preserve unsent drafts on refresh
  and show applied slider markers while drafts differ; do not add an "Active" row.
- Use one timer for **Copied** text and its temporary success styling.
- Invitations are automatic, reusable, and non-expiring. **Cycle** and operator
  **Stop and end session** revoke guest authority and replace the invitation;
  guest leave does not.
- Compatible limits must not pause/rearm control. Reject conflicts atomically
  with an actionable message. A stale-policy retry may retain explicitly sent
  settings, but must not send unsent drafts or extend an accepted motion lease
  through status refresh alone.

## Verification and specifications

- Read [.docs/specs/README.md](.docs/specs/README.md) and affected device-control
  requirements; retain their IDs. Update specs before durable requirement changes
  and confirm they capture intended behavior, not an accidental implementation.
- Update [the architecture diagram](.docs/diagrams/architecture.html) when project
  structure changes. Typically use one box per `src` project folder; pure
  libraries may be omitted. A target diagram is not implementation evidence.
- Use FastMoq when a test class can extend `MockerTestBase<T>` for its SUT;
  model/record tests are normally direct. Use AwesomeAssertions when
  straightforward, and `System.TimeProvider` for clock fakes.
- Name unit-test projects `<Assembly>.Tests` and integration projects
  `<Assembly>.IntegrationTests`; share helpers rather than mixing test levels.
- Start browser-affecting work with the fastest relevant focused check.
  Use browser tests for high-value visible integration behavior and unit/component
  tests for lower-level details. Prefer role, label, and stable `data-testid`
  locators; avoid positional, incidental-class, and DOM-structure selectors.
- Use isolated browser state and web-first assertions, not fixed delays or
  polling. Control clocks/external boundaries when needed. Assert markup, CSS
  classes, or geometry only for explicit visual/component contracts.
- Test owned behavior, not mostly dependency behavior, including Aspire AppHost.
- Before claiming migration completion, verify the
  [acceptance matrix](.docs/plans/direct-device-migration.md#acceptance-matrix).
  Report software conformance, live observations, and physical qualification
  separately. Simulator results do not prove Stock behavior.
- For device I/O, use synchronous `Span`/`ReadOnlySpan` and asynchronous
  `Memory`/`ReadOnlyMemory`. Never retain spans across `await`. Bound frames,
  avoid unnecessary copies/encoding, clear pooled secrets, and return buffers
  only after all consumers finish.

Use focused MTP commands; a full-suite run still requires approval:

```powershell
dotnet test --project tests/OssmSharp.Web.Application.Tests/OssmSharp.Web.Application.Tests.csproj --filter-method '*MethodName'
dotnet test --project tests/OssmSharp.Web.Application.Tests/OssmSharp.Web.Application.Tests.csproj --list-tests
dotnet test --project tests/OssmSharp.Web.Application.Tests/OssmSharp.Web.Application.Tests.csproj
dotnet test OssmSharp.slnx
```

Keep these paths aligned with project renames. Global approval rules still
apply to dependencies, schemas, branches, worktrees, commits, pushes, and
other-checkout changes.
