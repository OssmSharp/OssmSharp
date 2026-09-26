# Local Retro Web UI integration

The application consumes the existing **`RetroWebUi.Blazor` 0.2.0 NuGet
package**, not a fork or a copy of the widget implementation. It contains the
Razor wrappers, JavaScript initializer, Web Components and theme stylesheet.
Consumers need only .NET and the prebuilt package.

## Private package distribution

- `NuGet.Config` maps this package ID exclusively to
  `../retro-webui/artifacts/packages`. Other dependencies use nuget.org.
- `Directory.Packages.props` pins version `0.2.0`. Local builds do not pin the
  NuGet archive's SHA-256, which can differ after repacking on another machine.
- This private widget update adds a prominent tone-matched `rw-attention` glow
  with a moving gradient and reduced-motion and forced-colors-safe presentation.
- The authorized widget changes are in `../retro-webui`. Native `dotnet pack`
  links its canonical theme CSS; no Node tooling was run. All 18 JavaScript
  files and 17 declaration files match the reviewed `0.1.0` artifact, so Shadow
  DOM style hashes remain unchanged. Canonical theme CSS SHA-256 is
  `CE3C03FE6E761388F32D19450E62C76202FCE10AD1B16FD431A0BE5002447A77`.
- Keep the `.nupkg` in the local folder or an access-controlled internal feed.
  Do not commit the binary here or push/publish it to a public registry.
- NuGet metadata commands can query all configured sources despite source
  mapping. Use the local source explicitly when inspecting this private package.

For local development, pack the widgets in the sibling checkout into
`artifacts/packages` and restore this solution. NuGet may keep a cached package
with the same version; use a fresh package cache when checking a new local pack.
Review widget style changes and update the CSP style hashes together.

## Browser integration

The root documents load the package theme and select its `neon` preset. Blazor
discovers the package's initializer; no second registration script is installed.
Typed wrappers own Blazor actions. The JS-owned guest toggle and native
`.rw-slider` inputs retain their browser-side send/lease state. Both hosts
consume `ControlLayout` and static `ConnectionNotice` from
`OssmSharp.Web.Common`. The package owns their common styles; reconnect and
fatal notices take priority over ordinary operation notices. Native Blazor
reconnect states drive the display
without moving or replacing interactive DOM.

The library inserts constant styles into Shadow DOM. `RetroWidgetAssets`
contains the reviewed style hashes for this package version. The HTTP boundary
allows only those hashes, keeping arbitrary inline styles and scripts blocked.

Widget style changes require reviewing the CSP style hashes. No widget build
tooling is included in this repository.

### Inspect package style hashes

Use the Python inspector with an approved local `.nupkg`; it does not restore,
download, publish, extract, or install the package:

```powershell
uv run --with-requirements tests/browser/requirements.txt python tools/retro-widgets/style_hashes.py .artifacts/nuget-packages/retrowebui.blazor/0.2.0/retrowebui.blazor.0.2.0.nupkg
```

The package in the private source feed can also be inspected directly:

```powershell
uv run --with-requirements tests/browser/requirements.txt python tools/retro-widgets/style_hashes.py ../retro-webui/artifacts/packages/RetroWebUi.Blazor.0.2.0.nupkg
```

Prerequisites are `uv`, Python with the Playwright version pinned in
`tests/browser/requirements.txt` (currently 1.61.0), and an existing browser. By
default, the inspector uses the installed Microsoft Edge. Use
`--browser chromium` for an existing Playwright Chromium installation or
`--browser firefox` for an existing Playwright Firefox installation. The tool
installs nothing.

The inspector serves only the archive's `staticwebassets/widgets` assets through
intercepted synthetic-origin routes; external requests are blocked. It observes
`customElements.define` while importing `index.js`, creates each discovered
element, and reads open Shadow DOM `<style>` `textContent` after two animation
frames. Python hashes the exact UTF-8 text, without trimming or newline
normalization. JavaScript templates are executed by the browser, not parsed or
evaluated in Python. Only run it against approved packages: this is a widget
inspection tool, not a sandbox for untrusted code.

Successful stdout is JSON containing `packageSha256` (hex), `elements` (names
and their `styleHashes`), and `styleHashes` (sorted unique base64 SHA-256
digests across all elements). Failure produces a diagnostic on stderr and a
nonzero exit code, with no partial JSON. The results describe settled,
default-state widgets, not every attribute/interaction state or transient DOM
mutation. Closed or missing Shadow DOM is an inspection error.

Review the package checksum, element list, and style values together before
manually updating any approved package/CSP pins. For a reviewed style digest,
the CSP source-expression form is `'sha256-<base64 digest>'`. An observed empty
style is reported as `47DEQpj8HBSa+/TImW+5JCeuQeRkm5NMpJWZG3hSuFU=`; its
presence is evidence to investigate, not automatic permission to add it. The
tool neither authorizes scripts nor changes application files or CSP.

Run the focused regression checks with:

```powershell
uv run --with-requirements tests/browser/requirements.txt python -m unittest discover -s tests/browser -p test_style_hashes.py
```

These checks use Edge for synthetic packages and compare Edge with Firefox for
the installed local package. The package comparison is explicitly skipped when
that package is absent; missing required browsers are failures.

Blazor owns all rendered guest text. Browser input handling reports display
state through the existing interop reference; it never adds, removes or replaces
children in the interactive render tree. The startup script and controller
imports use the same fingerprinted asset version.

### Troubleshoot CSP console warnings

A source such as `moz-extension://.../inject/index.js` belongs to a browser
extension, not the application. Disable the extension for the loopback site and
retry in a clean browser profile. Do not copy extension-related
console-suggested hashes into the application's CSP.

The `47DE...` digest above hashes the empty string; it alone does not indicate
widget style drift. The application CSP correctly blocks an injected empty
`<style>` and an unapproved inline script. Checks in clean Edge and Firefox
profiles of the operator and invalid-invitation pages produced no
`securitypolicyviolation` events, with the widget Shadow DOM stylesheets
attached. Recalculate the hashes when the approved package changes; console
suggestions alone are not evidence of a package update.

Sources: the widget checkout's `docs/blazor.md` and `docs/themes.md`;
[NuGet source-mapping rules](https://learn.microsoft.com/nuget/consume-packages/package-source-mapping).
