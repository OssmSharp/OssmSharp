"""Offline hash-tool checks: python -m unittest discover -s tests/browser -p test_style_hashes.py."""

import ast
import base64
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
import xml.etree.ElementTree as ET
from unittest.mock import patch
from zipfile import ZipFile


ROOT = Path(__file__).resolve().parents[2]
TOOL = ROOT / "tools/retro-widgets/style_hashes.py"
VERSION = ET.parse(ROOT / "src/SharedUi/SharedUi.csproj").find(".//PackageReference[@Include='RetroWebUi.Blazor']").attrib["Version"].strip("[]")
PACKAGE = ROOT / f".artifacts/nuget-packages/retrowebui.blazor/{VERSION}/retrowebui.blazor.{VERSION}.nupkg"


class StyleHashesTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.package = Path(self.directory.name) / "widgets.nupkg"

    def run_tool(self, package=None, *args):
        return subprocess.run(
            [sys.executable, str(TOOL), str(package or self.package), *args],
            capture_output=True, text=True, encoding="utf-8", timeout=45,
        )

    def write_package(self, source, **assets):
        with ZipFile(self.package, "w") as archive:
            archive.writestr("staticwebassets/widgets/index.js", source)
            for name, content in assets.items():
                archive.writestr("staticwebassets/widgets/" + name, content)

    def assert_failure(self, result, message):
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(result.stdout, "")
        self.assertIn(message, result.stderr.lower())
        self.assertNotIn("Traceback", result.stderr)

    def test_retained_tool_is_python_without_source_evaluation(self):
        source = TOOL.read_text(encoding="utf-8")
        tree = ast.parse(source)
        self.assertFalse(any(
            isinstance(node, ast.Call) and isinstance(node.func, ast.Name)
            and node.func.id in {"eval", "exec"} for node in ast.walk(tree)
        ))
        self.assertFalse(list(TOOL.parent.glob("*.mjs")))
        self.assertFalse(list(TOOL.parent.glob("*.js")))

    def test_missing_package_is_clear_failure(self):
        self.assert_failure(self.run_tool(), "package")

    def test_help_describes_browser_without_loading_package(self):
        result = subprocess.run(
            [sys.executable, str(TOOL), "--help"],
            capture_output=True, text=True, timeout=10,
        )
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn("--browser", result.stdout)
        self.assertIn("package", result.stdout)

    def test_invalid_browser_is_usage_error(self):
        result = self.run_tool(None, "--browser", "unknown")
        self.assertEqual(result.returncode, 2)
        self.assert_failure(result, "invalid choice")

    def test_missing_browser_is_clear_failure_without_installing(self):
        self.write_package("export {};")
        with patch.dict(os.environ, {"PLAYWRIGHT_BROWSERS_PATH": self.directory.name}):
            result = self.run_tool(None, "--browser", "chromium")
        self.assert_failure(result, "browser/module inspection failed")
        self.assertEqual(list(Path(self.directory.name).iterdir()), [self.package])

    def test_invalid_archive_is_clear_failure(self):
        self.package.write_text("not a zip", encoding="utf-8")
        self.assert_failure(self.run_tool(), "archive")

    def test_missing_entry_module_is_clear_failure(self):
        with ZipFile(self.package, "w") as archive:
            archive.writestr("other.txt", "")
        self.assert_failure(self.run_tool(), "index.js")

    def test_duplicate_asset_is_rejected(self):
        self.write_package("export {};")
        with ZipFile(self.package, "a") as archive:
            import warnings
            with warnings.catch_warnings():
                warnings.simplefilter("ignore", UserWarning)
                archive.writestr("staticwebassets/widgets/index.js", "export {};")
        self.assert_failure(self.run_tool(), "duplicate")

    def test_module_failure_is_clear_failure(self):
        self.write_package("throw new Error('fixture module failure');")
        self.assert_failure(self.run_tool(), "fixture module failure")

    def test_missing_import_is_clear_failure(self):
        self.write_package("import './missing.js';")
        self.assert_failure(self.run_tool(), "missing.js")

    def test_external_request_is_blocked_and_reported(self):
        self.write_package("await fetch('https://example.invalid/not-allowed');")
        self.assert_failure(self.run_tool(), "blocked")

    def test_constructor_failure_is_not_partial_success(self):
        self.write_package("""
customElements.define('fixture-broken', class extends HTMLElement {
    constructor() { super(); throw new Error('fixture constructor failure'); }
});
""")
        self.assert_failure(self.run_tool(), "fixture constructor failure")

    def test_no_registered_elements_is_failure(self):
        self.write_package("export {};")
        self.assert_failure(self.run_tool(), "no custom elements")

    def test_browser_text_content_is_hashed_exactly_including_empty(self):
        css = "/* café 🎛 */\r\n:host { color: red; }\n"
        self.write_package("""
const css = %s;
for (const name of ['fixture-zebra', 'fixture-alpha']) {
    customElements.define(name, class extends HTMLElement {
        constructor() {
            super();
            const root = this.attachShadow({mode: 'open'});
            for (const text of [css, '', css]) {
                const style = document.createElement('style');
                style.textContent = text;
                root.append(style);
            }
        }
    });
}
""" % json.dumps(css))
        result = self.run_tool()
        self.assertEqual(result.returncode, 0, result.stderr)
        output = json.loads(result.stdout)
        expected = sorted(
            base64.b64encode(hashlib.sha256(text.encode("utf-8")).digest()).decode("ascii")
            for text in ["", css]
        )
        self.assertEqual(output["styleHashes"], expected)
        self.assertEqual(output["elements"], [
            {"name": name, "styleHashes": expected}
            for name in ["fixture-alpha", "fixture-zebra"]
        ])
        self.assertEqual(output["packageSha256"], hashlib.sha256(self.package.read_bytes()).hexdigest())

    @unittest.skipUnless(PACKAGE.is_file(), "approved local package is not installed")
    def test_installed_package_edge_and_firefox_agree(self):
        outputs = []
        for browser in ["edge", "firefox"]:
            with self.subTest(browser=browser):
                result = self.run_tool(PACKAGE, "--browser", browser)
                self.assertEqual(result.returncode, 0, result.stderr)
                output = json.loads(result.stdout)
                self.assertEqual(len(output["elements"]), 12)
                self.assertIn("rw-button", [element["name"] for element in output["elements"]])
                self.assertTrue(all(element["styleHashes"] for element in output["elements"]))
                self.assertEqual(output["styleHashes"], sorted(set(output["styleHashes"])))
                self.assertEqual(output["packageSha256"], hashlib.sha256(PACKAGE.read_bytes()).hexdigest())
                outputs.append(output)
        self.assertEqual(len(outputs), 2)
        self.assertEqual(outputs[0], outputs[1])


if __name__ == "__main__":
    unittest.main()
