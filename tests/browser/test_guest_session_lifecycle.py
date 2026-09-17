import unittest
from pathlib import Path

from playwright.sync_api import sync_playwright


class GuestSessionLifecycleTests(unittest.TestCase):
    def setUp(self):
        self.playwright = sync_playwright().start()
        self.browser = self.playwright.chromium.launch(channel="msedge")
        self.page = self.browser.new_page()
        self.page.clock.install()
        root = Path(__file__).resolve().parents[2]
        module = (root / "src" / "OSSMDotNet.GuestWeb" / "wwwroot" / "guest-session.js").read_text()
        controls = "".join(
            f'<input data-input="{name}" min="0" max="100" value="{value}">'
            f'<svg><line data-current-marker="{name}"/><line data-sent-marker="{name}"/></svg>'
            for name, value in (("speed", 10), ("stroke", 10), ("depth", 20))
        )
        html = (
            '<main>'
            f'{controls}'
            '<button data-send-button>Send</button><button data-stop-button>Stop</button>'
            '<input data-auto-send type="checkbox"></main>'
        )
        self.page.route("https://guest-controls.invalid/**", lambda route: route.fulfill(
            content_type="text/javascript" if route.request.url.partition("?")[0].endswith(".js") else "text/html",
            body=module if route.request.url.partition("?")[0].endswith(".js") else html,
        ))

    def tearDown(self):
        self.browser.close()
        self.playwright.stop()

    def test_invitation_fragment_is_scrubbed_and_consumed_once_for_valid_and_invalid_fragments(self):
        for case, (fragment, expected) in enumerate(
            (("#invite=private-proof", "private-proof"), ("#invite=one&other=two", None))
        ):
            with self.subTest(fragment=fragment):
                self.page.goto("https://guest-controls.invalid/join/test" + fragment)
                result = self.page.evaluate("""async (caseId) => {
                    const module = await import(`/guest-session.js?case=${caseId}`);
                    return {
                        first: module.takeInvitation(),
                        second: module.takeInvitation(),
                        hash: location.hash,
                        path: location.pathname
                    };
                }""", case)

                self.assertEqual(
                    {"first": expected, "second": None, "hash": "", "path": "/join/test"},
                    result,
                )

    def test_blur_and_hidden_transitions_request_one_stop_and_do_not_send_more_motion(self):
        for event in ("blur", "hidden"):
            with self.subTest(event=event):
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
                            if (method === "SubmitRun") window.calls.push(input);
                            if (method === "StopRun") window.stops++;
                            return { ok: true, code: "ok" };
                        }
                    });
                    window.module.updateAuthority(window.authority);
                }""")
                self.page.get_by_role("button", name="Send", exact=True).click()
                self.page.clock.run_for(150)
                baseline_calls, baseline_stops = self.page.evaluate("[window.calls, window.stops]")
                if event == "hidden":
                    self.page.evaluate("""async () => {
                        Object.defineProperty(document, "visibilityState", { configurable: true, value: "hidden" });
                        document.dispatchEvent(new Event("visibilitychange"));
                        await Promise.resolve();
                        await Promise.resolve();
                    }""")
                else:
                    self.page.evaluate("""async () => {
                        window.dispatchEvent(new Event("blur"));
                        await Promise.resolve();
                        await Promise.resolve();
                    }""")
                self.page.clock.run_for(250)

                calls, stops = self.page.evaluate("[window.calls, window.stops]")
                self.assertEqual(baseline_stops + 1, stops)
                self.assertEqual(baseline_calls, calls)
                self.assertEqual(10, calls[0]["desired"]["speed"])


if __name__ == "__main__":
    unittest.main()
