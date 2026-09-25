# Repository instructions

## Stack

- You must use .NET 10, C#, and **ASP.NET Core Blazor Interactive Server + minimal APIs**.
- You must use **xUnit v3 + MTP v2**, not MSTest, xUnit v2 or VSTest.
- You must use **Aspire AppHost + ServiceDefaults** and follow the
  [first-app guide](https://aspire.dev/get-started/first-app/?aspire-lang=csharp).
- You must use `aspire run` to build and orchestrate services; you must not use
  a custom service launcher.
- You should prefer native .NET/Aspire best practices and remove redundant
  boilerplate.
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

## Coding

- You must use `OSSMDotNet.*` for reusable namespaces and the web-specific
  namespaces `OSSMDotNet.AdminWeb`, `OSSMDotNet.GuestWeb`,
  `OSSMDotNet.ControlService`, and `OSSMDotNet.WebCommon`.
- `OSSMDotNet.Cli` must use the control library and remain unrelated to the
  guest and admin frontend system; it must not have references to terms such as
  "admin".

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
  be warning/yellow. Disabled buttons must be grayscale. An available **Allow
  control** action for a waiting guest must have a green attention glow; you
  must respect reduced motion and preserve device-readiness and safety checks.
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

- You must keep GuestWeb, AdminWeb, and ControlService as **separate processes**.
- ControlService must exclusively own sessions, policy, timers, the single writer
  and device bridge. AdminWeb and GuestWeb must call it through `/hubs/control/v1`.
- AdminWeb must remain unauthenticated and bound to `127.0.0.1`; you must retain
  Host/Origin protection and must not expose/proxy it remotely. It must authenticate
  server-to-server with its generated service key.
- AdminWeb listener configuration must not add/replace endpoints through
  Kestrel, hosting URL precedence or configuration reload; use
  `OSSM:Admin:AdminPort`.
- ControlService must authenticate AdminWeb and GuestWeb with distinct generated
  service keys. Guest invitation/session capabilities and expiry must remain
  independently enforced; service authentication must not weaken guest
  authorization or arm motion.
- GuestWeb must expose only Join/input/Stop/leave/scoped status, with no
  operator/device access. Browser and Blazor state must never authorize motion.
- ControlService must support an explicitly configured remote HTTPS/WSS endpoint;
  you must not bypass certificate validation, expose service credentials to browsers,
  trust forwarded headers, or introduce a proxy by default.
- Browser and Blazor state must be treated as untrusted; neither may authorize motion.
- You must use one-box, accountless capabilities and one-click invitations;
  you must not add accounts, a database or cloud services.
- You must reuse `ossm-rs`'s `ossm-motion` through the admin-owned Rust adapter.
- You must work in the simulator only; you must not edit sibling repositories,
  flash firmware, or perform hardware or physical qualification.
- Stop/lock must override pending input. Only admin may unlock, into **paused**.
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
  dotnet test OssmWebControls.slnx
  dotnet test --project tests/OSSMDotNet.ControlCore.Tests/ControlCore.Tests.csproj
  dotnet test --project tests/OSSMDotNet.ControlCore.Tests/ControlCore.Tests.csproj --filter-method '*MethodName'
  dotnet test --project tests/OSSMDotNet.ControlCore.Tests/ControlCore.Tests.csproj --list-tests
  ```
- You must never remove failing tests or claim unrun checks passed witout justification.
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
