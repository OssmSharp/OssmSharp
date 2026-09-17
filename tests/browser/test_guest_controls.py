import unittest
from pathlib import Path

from playwright.sync_api import expect, sync_playwright


class GuestControlTests(unittest.TestCase):
    def setUp(self):
        self.playwright = sync_playwright().start()
        self.browser = self.playwright.chromium.launch(channel="msedge")
        self.page = self.browser.new_page()
        self.page.clock.install()
        root = Path(__file__).resolve().parents[2]
        modules = {
            "guest-session.js": (root / "src" / "OSSMDotNet.GuestWeb" / "wwwroot" / "guest-session.js").read_text(),
            "connection-ui.js": (root / "src" / "OSSMDotNet.WebCommon" / "wwwroot" / "connection-ui.js").read_text(),
        }
        controls = "".join(
            f'<input data-input="{name}" min="0" max="100" value="{value}">'
            f'<svg><line data-current-marker="{name}"/><line data-sent-marker="{name}"/></svg>'
            for name, value in (("speed", 10), ("stroke", 10), ("depth", 20))
        )
        html = f'<div id="app-boot-status"></div><div id="components-reconnect-modal" class="components-reconnect-hide"></div><main>{controls}<button data-send-button>Send</button><button data-stop-button>Stop</button><input data-auto-send type="checkbox"></main>'
        self.page.route("https://guest-controls.invalid/**", lambda route: route.fulfill(
            content_type="text/javascript" if route.request.url.endswith(".js") else "text/html",
            body=modules[route.request.url.rsplit("/", 1)[1]] if route.request.url.endswith(".js") else html,
            headers={"Content-Security-Policy": "default-src 'self'; script-src 'self'"},
        ))
        self.page.goto("https://guest-controls.invalid/join/test")
        self.page.evaluate("""async () => {
            window.module = await import("/guest-session.js");
            window.calls = [];
            window.stops = 0;
            window.authority = {
                joined: true, ready: true, fresh: true, policyVersion: 1,
                grant: { id: "grant-one", epoch: "epoch-one", policyVersion: 1, validForMs: 750 },
                ackSeq: 0, limits: { speedCap: 100, maximumDepth: 100 }
            };
            window.module.attach(document.querySelector("main"), {
                async invokeMethodAsync(method, input) {
                    if (method === "SubmitRun") {
                        window.calls.push(input);
                        if (window.rejectPolicyOnce) {
                            window.rejectPolicyOnce = false;
                            window.authority.policyVersion = 2;
                            window.authority.grant = { id: "grant-two", epoch: "epoch-one", policyVersion: 2, validForMs: 750 };
                            window.module.updateAuthority(window.authority);
                            return { ok: false, code: "policy-invalid" };
                        }
                    }
                    if (method === "StopRun") window.stops++;
                    return { ok: true, code: "ok" };
                }
            });
            window.module.updateAuthority(window.authority);
        }""")

    def tearDown(self):
        self.browser.close()
        self.playwright.stop()

    def test_policy_refresh_keeps_the_explicitly_sent_tuple_without_stopping(self):
        self.page.evaluate("window.rejectPolicyOnce = true")
        self.page.get_by_role("button", name="Send", exact=True).click()
        self.page.locator('[data-input="speed"]').fill("25")
        self.page.locator('[data-input="speed"]').dispatch_event("input")
        self.page.clock.run_for(250)
        calls, stops = self.page.evaluate("[window.calls, window.stops]")
        self.assertEqual(0, stops)
        self.assertGreaterEqual(len(calls), 2)
        self.assertEqual(1, calls[0]["policyVersion"])
        self.assertEqual(2, calls[1]["policyVersion"])
        self.assertEqual(calls[0]["desired"], calls[1]["desired"])
        self.assertEqual(10, calls[1]["desired"]["speed"])
        self.assertGreater(calls[1]["seq"], calls[0]["seq"])

    def test_policy_refresh_does_not_send_unsent_slider_edits(self):
        self.page.locator('[data-input="speed"]').fill("25")
        self.page.locator('[data-input="speed"]').dispatch_event("input")
        self.page.evaluate("""() => {
            window.authority.policyVersion = 2;
            window.authority.grant = { id: "grant-two", epoch: "epoch-one", policyVersion: 2, validForMs: 750 };
            window.module.updateAuthority(window.authority);
        }""")
        self.page.clock.run_for(250)
        self.assertEqual([], self.page.evaluate("window.calls"))
        self.assertEqual(0, self.page.evaluate("window.stops"))

    def test_disconnect_disables_controls_and_new_authority_cannot_resume_them(self):
        self.page.evaluate("window.module.disconnect()")
        self.page.evaluate("window.module.updateAuthority(window.authority)")
        expect(self.page.get_by_role("button", name="Send", exact=True)).to_be_disabled()
        expect(self.page.locator("[data-auto-send]")).to_be_disabled()
        self.page.clock.run_for(250)
        self.assertEqual([], self.page.evaluate("window.calls"))
        self.assertEqual(0, self.page.evaluate("window.stops"))

    def test_native_reconnection_notifications_discard_guest_control_once(self):
        result = self.page.evaluate("""async () => {
            const { initialize } = await import("/connection-ui.js");
            let losses = 0;
            const connection = initialize(() => { losses++; window.module.disconnect(); });
            connection.ready();
            const reconnect = document.getElementById("components-reconnect-modal");
            for (const state of ["show", "retrying", "hide"]) {
                reconnect.className = "components-reconnect-" + state;
                reconnect.dispatchEvent(new CustomEvent("components-reconnect-state-changed", { detail: { state } }));
            }
            return { losses, bootHidden: document.getElementById("app-boot-status").hidden,
                     rejected: reconnect.classList.contains("components-reconnect-rejected"),
                     retries: connection.circuit.reconnectionOptions.maxRetries };
        }""")
        self.assertEqual({"losses": 1, "bootHidden": True, "rejected": True, "retries": 3}, result)
        expect(self.page.get_by_role("button", name="Send", exact=True)).to_be_disabled()
        self.assertEqual(0, self.page.evaluate("window.stops"))

    def test_operator_reconnection_can_hide_the_notice_without_guest_authority(self):
        result = self.page.evaluate("""async () => {
            const { initialize } = await import("/connection-ui.js");
            const connection = initialize();
            const reconnect = document.getElementById("components-reconnect-modal");
            for (const state of ["show", "hide"]) {
                reconnect.className = "components-reconnect-" + state;
                reconnect.dispatchEvent(new CustomEvent("components-reconnect-state-changed", { detail: { state } }));
            }
            const recovered = reconnect.classList.contains("components-reconnect-hide");
            connection.failed();
            return { recovered, failed: reconnect.classList.contains("components-reconnect-rejected"),
                     hidden: reconnect.classList.contains("components-reconnect-hide") };
        }""")
        self.assertEqual({"recovered": True, "failed": True, "hidden": False}, result)


if __name__ == "__main__":
    unittest.main()
