"""Inspect an approved local widget package without installing it or changing CSP."""

import argparse
import base64
import hashlib
from io import BytesIO
import json
from pathlib import Path, PurePosixPath
import sys
from urllib.parse import unquote, urlsplit
from zipfile import BadZipFile, ZipFile


ORIGIN = "https://retro-widgets.invalid"
ASSET_PREFIX = "staticwebassets/widgets/"


def read_package(path):
    try:
        content = path.read_bytes()
    except OSError as error:
        raise ValueError(f"Cannot read package '{path}': {error}") from error
    assets = {}
    try:
        with ZipFile(BytesIO(content)) as archive:
            for entry in archive.infolist():
                if not entry.filename.startswith(ASSET_PREFIX) or entry.is_dir():
                    continue
                name = entry.filename[len(ASSET_PREFIX):]
                if "\\" in name or str(PurePosixPath(name)) != name or ".." in PurePosixPath(name).parts:
                    raise ValueError(f"Invalid widget asset path: {name}")
                if name in assets:
                    raise ValueError(f"Duplicate widget asset: {name}")
                assets[name] = archive.read(entry)
    except (BadZipFile, OSError, RuntimeError, NotImplementedError) as error:
        raise ValueError(f"Invalid package archive: {error}") from error
    if "index.js" not in assets:
        raise ValueError(f"Package is missing {ASSET_PREFIX}index.js")
    return hashlib.sha256(content).hexdigest(), assets


def inspect_styles(assets, browser_name):
    try:
        from playwright.sync_api import Error, sync_playwright
    except ImportError as error:
        raise ValueError("Python Playwright is required; see tests/browser/requirements.txt.") from error

    failures = []
    with sync_playwright() as playwright:
        try:
            browser_type = playwright.firefox if browser_name == "firefox" else playwright.chromium
            options = {"channel": "msedge"} if browser_name == "edge" else {}
            with browser_type.launch(headless=True, **options) as browser:
                context = browser.new_context(service_workers="block")
                context.set_default_timeout(15_000)

                def route_request(route):
                    url = urlsplit(route.request.url)
                    if f"{url.scheme}://{url.netloc}" != ORIGIN:
                        failures.append(f"Blocked external request: {route.request.url}")
                        route.abort()
                        return
                    if url.path == "/":
                        route.fulfill(content_type="text/html", body="<!doctype html><html><body></body></html>")
                        return
                    name = unquote(url.path.removeprefix("/widgets/"))
                    if not url.path.startswith("/widgets/") or name not in assets:
                        failures.append(f"Missing package asset: {url.path}")
                        route.abort()
                        return
                    content_type = {
                        ".js": "text/javascript",
                        ".css": "text/css",
                        ".json": "application/json",
                    }.get(PurePosixPath(name).suffix, "application/octet-stream")
                    route.fulfill(content_type=content_type, body=assets[name])

                def block_socket(socket):
                    failures.append(f"Blocked WebSocket: {socket.url}")
                    socket.close()

                context.route("**/*", route_request)
                context.route_web_socket("**/*", block_socket)
                page = context.new_page()
                page.on("pageerror", lambda error: failures.append(str(error)))
                page.goto(ORIGIN, wait_until="load")
                # Browser execution resolves imported constants and preserves DOM text;
                # never try to reconstruct JavaScript templates in Python.
                page.evaluate("""() => {
                    window.inspection = null;
                    (async () => {
                        const names = [];
                        const originalDefine = customElements.define;
                        customElements.define = function(name, constructor, options) {
                            originalDefine.call(this, name, constructor, options);
                            names.push(name);
                        };
                        try {
                            await import('/widgets/index.js');
                        } finally {
                            customElements.define = originalDefine;
                        }
                        if (!names.length) throw new Error('No custom elements registered by index.js');
                        const elements = [...new Set(names)].sort().map(name => {
                            const element = document.createElement(name);
                            document.body.append(element);
                            return {name, element};
                        });
                        await new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)));
                        function stylesIn(root) {
                            const texts = [...root.querySelectorAll('style')].map(style => style.textContent);
                            for (const child of root.querySelectorAll('*')) {
                                if (child.shadowRoot) texts.push(...stylesIn(child.shadowRoot));
                            }
                            return texts;
                        }
                        return elements.map(({name, element}) => {
                            if (!element.shadowRoot) throw new Error(`${name} has no open Shadow DOM`);
                            return {name, styles: stylesIn(element.shadowRoot)};
                        });
                    })().then(
                        elements => window.inspection = {elements},
                        error => window.inspection = {error: String(error)}
                    );
                }""")
                page.wait_for_function("window.inspection !== null")
                result = page.evaluate("window.inspection")
                if result.get("error"):
                    failures.append(result["error"])
                if failures:
                    raise ValueError("; ".join(failures))
                return result["elements"]
        except Error as error:
            details = "; ".join(failures)
            raise ValueError(f"Browser/module inspection failed ({browser_name}): {details} {error}") from error


def report(package, browser):
    package_hash, assets = read_package(package)
    elements = []
    for element in inspect_styles(assets, browser):
        hashes = sorted({
            base64.b64encode(hashlib.sha256(text.encode("utf-8")).digest()).decode("ascii")
            for text in element["styles"]
        })
        elements.append({"name": element["name"], "styleHashes": hashes})
    return {
        "packageSha256": package_hash,
        "elements": elements,
        "styleHashes": sorted({value for element in elements for value in element["styleHashes"]}),
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("package", type=Path, help="Path to the approved local .nupkg (never downloaded).")
    parser.add_argument(
        "--browser", choices=["edge", "chromium", "firefox"], default="edge",
        help="Installed browser to use (default: edge). No browser is installed automatically.",
    )
    args = parser.parse_args()
    try:
        result = report(args.package, args.browser)
    except (ValueError, OSError) as error:
        print(f"style_hashes: {error}", file=sys.stderr)
        return 1
    except KeyboardInterrupt:
        print("style_hashes: interrupted", file=sys.stderr)
        return 130
    print(json.dumps(result, indent=2))
    return 0


if __name__ == "__main__":
    sys.exit(main())
