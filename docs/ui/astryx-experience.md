# Admin and guest experience

The admin flow answers three questions in order: is the device connected, has the guest joined, and can they send settings? The heading follows session state. The invitation starts expanded and folds away once a guest is connected; the user can open it explicitly. Session actions remain visible rather than being hidden behind a disclosure. Last settings appear beside those actions only when a device and guest are connected. Destructive guest actions keep their existing confirmation.

The guest flow is choose a pattern, adjust percentages, inspect the selected preview, and send. The action bar stays visible while scrolling. Auto send is explicit and retains the existing rule that changing patterns pauses it. Sent and current values stay beside each setting; unknown speed at zero stroke remains unknown. The preview describes selected settings, not confirmed physical movement.

## Integration

Both hosts keep their Blazor pages, authenticated circuits, server authorization, API clients, and guest controller. Actual Astryx React 19 buttons render in `ossm-button` shadow roots. This boundary lets Blazor own the light DOM (including labels and event handlers), React own its shadow DOM, and the guest controller continue using synchronous `.disabled`, `data-*`, and `aria-pressed` updates. Native range inputs retain their browser keyboard and integer-step behavior. The bridge uses a shared constructed stylesheet, not a stylesheet copy per button. A theme mutation updates button styling without replacing the focused button.

Native composed click events cross the shadow boundary and reach the existing listeners. Disabled Astryx buttons do not emit user clicks. Stop, reset, permission checks, invitation redemption, and reconnect behavior use the existing application code. The disconnect path explicitly locks custom buttons as well as native inputs.

## Build and verify

From `ui`, run `npm ci`, `npm run build`, and `npm test`. Browser tests use Playwright's installed Chromium by default; set `PLAYWRIGHT_CHROMIUM_EXECUTABLE` to use an existing browser. Generated JS, CSS, and license notices are committed under `src/OssmSharp.Web.Common/wwwroot/astryx` so .NET builds and published downloads need no Node installation or external CDN at runtime. Do not edit generated files; rebuild from `ui/src/elements.jsx` and the pinned lockfile.

Build both web projects with .NET 10. For controller tests, run `node --experimental-vm-modules --test src/OssmSharp.Web.GuestWeb/tests/*.test.mjs`. Full-page flows are in `guest-browser.mjs`; set `PLAYWRIGHT_MODULE` to the absolute `ui/package.json` path so the existing fixture can resolve Playwright. These use an isolated test API, not a Bluetooth device.

Astryx is beta. Package versions are pinned; upgrades require rebuilding the assets and rerunning keyboard, disabled-state, theme, and full-page session checks. This is a hybrid UI rebuild, not a migration of the session backend to a React SPA.
