# Browser regression checks

The Aspire integration test runs the operator/guest journey in isolated Firefox
and Edge profiles. It covers automatic reusable invitations, single-guest
admission, rejoining after leave with fresh approval, operator End rotating the
invitation, link cycling and revocation, invalid links, applied/draft slider
markers, live limits without pausing, Stop and reload. It also checks shared
responsive layout, top-right connection/error notices, button names/tooltips,
grayscale disabled states, reduced-motion-safe approval glow and timed Copy
success styling. The journey also closes its own page's real WebSocket: guest
control stays discarded, the invitation remains reusable, and operator transport
recovery does not arm control. Browser page errors, console errors and
`securitypolicyviolation` events fail the check; the optional favicon's 404 is
ignored. CSP events are checked even when the browser reports them as warnings.
No browser profile or invitation credentials are persisted.

The guest startup check also removes a temporary, test-owned head element as
Blazor replaces the prerendered title. This reproduces extension-style DOM
changes without installing an extension or relaxing CSP. It verifies that the
mutation ran, the native DOM method was restored, invitation rendering still
works, and interactive navigation updates the page title. The guest stylesheet
stays after the interactive `HeadOutlet` to provide a stable insertion sibling.

Requires Python (verified with 3.14), installed Microsoft Edge, and Playwright:

```powershell
python -m pip install -r tests\browser\requirements.txt
python -m playwright install firefox
$env:TESTINGPLATFORM_TELEMETRY_OPTOUT = '1'
dotnet test --project tests\AppHost.Tests\AppHost.Tests.csproj --filter-method '*AspireStartsIsolatedServicesAndTheRealSimulatorSupportsTheOperatorJourney' '-bl:artifacts\browser-{}.binlog'
```

The test starts and stops its own Aspire application and simulator. It does not
connect to physical hardware or interact with an existing user browser.

The smaller browser-module tests use a fake interop boundary and Playwright's
clock, not an application server or simulator. They cover a policy refresh
between Send and acceptance, retaining only explicitly sent values, and
fail-closed disconnection:

```powershell
python -m unittest discover -s tests\browser -p test_guest_controls.py
```

To check an already running simulator application, supply its local endpoints
over stdin. The script creates and ends a guest session, so use an idle
instance:

```powershell
@{
    adminUrl = 'http://127.0.0.1:<admin-port>'
    guestOrigin = 'http://127.0.0.1:<guest-port>'
    browser = 'firefox' # or 'edge'
} | ConvertTo-Json -Compress | python tests\browser\prototype_smoke.py
```
