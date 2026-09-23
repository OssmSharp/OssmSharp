# Repository instructions

## Stack

- .NET 10, C#, **ASP.NET Core Blazor Interactive Server + minimal APIs**.
- **xUnit v3 + MTP v2**; no MSTest, xUnit v2 or VSTest.
- **Aspire AppHost + ServiceDefaults**; follow the
  [first-app guide](https://aspire.dev/get-started/first-app/?aspire-lang=csharp).
- `aspire run` builds/orchestrates services. No custom service launcher.
- Prefer native .NET/Aspire best practices; remove redundant boilerplate.
- No Node.js tooling or scripts.
- Python with Playwright is permitted for browser regression checks and private
  widget CSP hash calculation. Keep application builds and orchestration native
  to .NET/Aspire. Manage it with `uv`. `python -m pip install` is strictly
  forbidden.
- Update `.docs\diagrams\architecture.html` with changes to project structure.
  Typically, each folder under `src` will have one box in the diagram, except
  for pure libraries that may have zero.
- Update `.docs/specs` at the start of making durable changes to requirements.
  Double-check before proceeding that the user's intent is honoured and the
  change does not mask or enshrine the wrong behaviour.

## Coding

- Reusable namespaces: `OSSMDotNet.*`. Web-specific: `OSSMDotNet.AdminWeb`,
  `OSSMDotNet.GuestWeb`, `OSSMDotNet.ControlService`, and
  `OSSMDotNet.WebCommon`.
- `OSSMDotNet.Cli` is a command-line tool that uses the control library. It is
  otherwise unrelated to the guest and admin frontend system, and must not have
  references to terms such as "admin".

## User interface conventions

- Operator and guest pages share `ControlLayout`, control sizing, spacing and
  notification behavior. Do not create parallel page shells or duplicate guest
  headings.
- Put connection, reconnection, operation and error notices at the top right,
  outside normal layout flow. Connection failures take precedence over other
  notices. Keep framework-managed connection UI outside interactive route DOM.
- On guest connection loss, show
  `Connection lost. Open the invitation link again.` as a standard danger
  `RwAlert` above live feedback; grayscale the remaining guest interface and
  hide the paused status message.
- Make shared widget styling changes in **RetroWebUi**, then consume the
  reviewed private package version here. Update its package checksum and verify
  CSP style hashes together. Do not patch copied assets or introduce
  app-specific versions of shared widget states.
- Use named semantic styles: waiting is warning/gold, connected and successful
  actions are success/green, and **Stop and end session** is warning/yellow.
  Disabled buttons are grayscale. An available **Allow control** action for a
  waiting guest has a green attention glow; respect reduced motion and preserve
  device-readiness and safety checks.
- Every button needs an accessible name and a useful tooltip. Icon-only buttons
  need an explicit accessible label; do not use an HTML `alt` attribute on
  buttons. Adjacent text inputs and buttons have matching heights.
- Put a limits widget's dirty status at its top right. Keep applied values
  marked on sliders while drafts differ, and preserve unsent edits during status
  refresh. Do not duplicate applied values in a separate "Active" row.
- Keep **Copied** text and temporary success styling on the same timer.
- Invitations are automatic, reusable and non-expiring. **Cycle** and operator
  **Stop and end session** revoke guest authority and replace the invitation;
  guest leave does not replace it.
- Applying compatible limits does not pause or rearm control. Reject conflicting
  limits atomically with an actionable message. A stale-policy retry may retain
  explicitly sent settings, never dispatch unsent slider edits or extend a
  previously accepted motion lease through status refresh alone.
- Startup, reconnect and refreshed invitations never arm or resume motion.

## Authority and safety

- Keep GuestWeb, AdminWeb, and ControlService as **separate processes**.
- Only ControlService owns sessions, policy, timers, the single writer and
  device bridge. AdminWeb and GuestWeb call it through `/hubs/control/v1`.
- AdminWeb is unauthenticated and always bound to `127.0.0.1`; retain
  Host/Origin protection and never expose/proxy it remotely. It authenticates
  server-to-server with its generated service key.
- AdminWeb listener configuration must not add/replace endpoints through
  Kestrel, hosting URL precedence or configuration reload; use
  `OSSM:Admin:AdminPort`.
- ControlService authenticates AdminWeb and GuestWeb with distinct generated
  service keys. Guest invitation/session capabilities and expiry remain
  independently enforced; service authentication must not weaken guest
  authorization or arm motion.
- GuestWeb exposes only Join/input/Stop/leave/scoped status; no operator/device
  access. Browser and Blazor state never authorize motion.
- ControlService supports an explicitly configured remote HTTPS/WSS endpoint; do
  not bypass certificate validation, expose service credentials to browsers,
  trust forwarded headers, or introduce a proxy by default.
- Browser and Blazor state are untrusted; neither authorizes motion.
- One-box, accountless capabilities, one-click invitations; no
  accounts/database/cloud.
- Reuse `ossm-rs`'s `ossm-motion` through the admin-owned Rust adapter.
- Simulator only: no sibling edits, firmware flashing, hardware or physical
  qualification.
- Stop/lock override pending input. Only admin unlocks, into **paused**.
- Startup/reconnect never arms/resumes. Cancellation never proves device I/O
  stopped.
- Preserve bounded work, execution ownership and safety checks when simplifying.
- Read `.docs/specs/README.md` and affected device-control requirements; retain
  IDs.

## Performance

- Use `Span`/`ReadOnlySpan` synchronously; `Memory`/`ReadOnlyMemory` for async
  I/O.
- Avoid needless copies, encoding round trips and hot-path allocations.
- Bound frames; clear pooled secrets; return buffers only after all consumers
  finish.
- No spans across `await`; prefer correctness over speculative optimization.

## Tests

- Tests should use FastMoq when the test class can extend `MockerTestBase<T>`
  where `T` is the SUT. Use InternalsVisibleTo if required. Model/record types
  are typically excluded.
- **FastMoq.Core** where useful; retain direct value tests and deterministic
  I/O/clock fakes using System.TimeProvider
- Tests before behavior; prove regressions fail before fixing.
- Run focused `dotnet test --project <project>`
- For browser-affecting changes, prefer the fastest applicable focused browser
  check before broader validation. Browser checks are suggested for high-value,
  user-visible integration behavior; prove lower-level details with component or
  unit tests.
- MTP v2 commands:
  ```powershell
  dotnet test OssmWebControls.slnx
  dotnet test --project tests\OSSMDotNet.ControlCore.Tests\ControlCore.Tests.csproj
  dotnet test --project tests\OSSMDotNet.ControlCore.Tests\ControlCore.Tests.csproj --filter-method '*MethodName'
  dotnet test --project tests\OSSMDotNet.ControlCore.Tests\ControlCore.Tests.csproj --list-tests
  ```
- Never remove failing tests or claim unrun checks passed.
- Separate test projects into unit tests and integration tests, with shared test
  helpers. Integration test projects must be named with the assembly they're
  testing followed by ".IntegrationTests". Unit test projects the same, but
  ending with ".Tests".
- Playwright: prefer `get_by_role`, `get_by_label`, and stable `data-testid`
  locators; avoid positional, incidental class, and DOM-structure selectors.
- Use web-first assertions for observable outcomes, not fixed delays or polling;
  isolate browser state and control clocks/external boundaries when needed.
- Assert markup, CSS classes, and pixel geometry only when they are an explicit
  component or visual contract.
- Do not write tests that predominantly validate behaviour in dependencies,
  including Aspire AppHosts.

## Boundaries

- Approved dependencies only: explicit, pinned, necessary.
- Use compatible installed tools; record actual versions.
- Branches, commits, pushes, schemas and other-checkout changes need explicit
  approval.
- No secrets in source. Use Podman, not Docker, when containers are authorized.
