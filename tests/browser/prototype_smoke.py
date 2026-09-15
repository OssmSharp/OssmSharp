import json
import re
import sys
import traceback
from urllib.parse import urlsplit

from playwright.sync_api import expect, sync_playwright


def install_transient_head_mutation(page):
    page.add_init_script("""
        document.addEventListener("DOMContentLoaded", () => {
            const transient = document.createElement("meta");
            transient.name = "temporary-head-regression";
            document.head.append(transient);
            window.__headMutation = { removed: false, restored: false };
            const removeChild = Node.prototype.removeChild;
            Node.prototype.removeChild = function(child) {
                // Model an extension removing its own head node during title hydration.
                if (this === document.head && child.nodeName === "TITLE" &&
                    transient.parentNode === this) {
                    Node.prototype.removeChild = removeChild;
                    window.__headMutation.restored = true;
                    removeChild.call(this, transient);
                    window.__headMutation.removed = true;
                    document.documentElement.dataset.headMutation = "complete";
                }
                return removeChild.call(this, child);
            };
        }, { once: true });
    """)


def assert_button_accessibility(page):
    for button in page.get_by_role("button").all():
        assert button.get_attribute("aria-label") or button.inner_text().strip(), "button-accessible-name"
        assert button.get_attribute("title"), "button-tooltip"
        if button.is_disabled():
            assert button.evaluate("element => getComputedStyle(element).filter") == "grayscale(1)"


def assert_top_right(page, notice):
    expect(notice).to_be_visible()
    box = notice.bounding_box()
    assert box and abs(box["y"] - 16) < 1
    assert abs(page.viewport_size["width"] - box["x"] - box["width"] - 16) < 1


def assert_connection_notices(page):
    notice = page.locator("#components-reconnect-modal")
    expect(notice).to_be_hidden()
    for state, text in (("show", "Rejoining the server..."), ("retrying", "Rejoining the server..."),
                        ("failed", "Connection lost."), ("rejected", "Connection lost.")):
        notice.evaluate("(element, state) => element.className = 'rw-notice rw-connection-notice components-reconnect-' + state", state)
        message = notice.locator(".rw-reconnect-pending" if state in ("show", "retrying") else ".rw-reconnect-failed")
        expect(message).to_be_visible()
        expect(message).to_contain_text(text)
        assert_top_right(page, notice)
        expect(page.locator(".rw-operation-notice:visible")).to_have_count(0)
    notice.evaluate("element => element.className = 'rw-notice rw-connection-notice components-reconnect-hide'")
    fatal = page.locator("#blazor-error-ui")
    fatal.evaluate("element => { element.style.display = 'block'; }")
    assert_top_right(page, fatal)
    expect(page.locator(".rw-operation-notice:visible")).to_have_count(0)
    fatal.evaluate("element => element.style.removeProperty('display')")
    expect(fatal).to_be_hidden()


def assert_mobile_layout(page):
    page.set_viewport_size({"width": 390, "height": 844})
    assert page.evaluate("document.documentElement.scrollWidth <= innerWidth + 1"), "horizontal-overflow"
    page.set_viewport_size({"width": 1280, "height": 1000})


def assert_transport_loss(page, guest):
    assert page.evaluate("window.__testSocket?.readyState") == 1
    page.evaluate("window.__testSocket.close()")
    expect(page.locator("html")).to_have_attribute("data-reconnect-started", "true")
    notice = page.locator("#components-reconnect-modal")
    if guest:
        expect(notice).to_have_class(re.compile(r"\bcomponents-reconnect-(rejected|failed|resume-failed)\b"), timeout=12000)
        assert_top_right(page, notice)
        expect(notice.locator(".rw-reconnect-failed")).to_contain_text("Connection lost.")
        expect(page.locator(".rw-operation-notice:visible")).to_have_count(0)
    else:
        expect(page.locator("html")).to_have_attribute("data-reconnect-state", "hide", timeout=12000)
        expect(notice).to_be_hidden()


def main():
    settings = json.loads(sys.stdin.readline())
    stage = "starting-browser"
    errors = []
    csp_violations = []
    invitation = ""
    invitations = []
    try:
        with sync_playwright() as playwright:
            if settings.get("browser") == "firefox":
                browser = playwright.firefox.launch(headless=True)
            else:
                browser = playwright.chromium.launch(channel="msedge", headless=True)
            context = browser.new_context(viewport={"width": 1280, "height": 1000})
            context.expose_binding(
                "__recordCspViolation", lambda source, directive: csp_violations.append(directive)
            )
            context.add_init_script("""
                window.WebSocket = class extends WebSocket {
                    constructor(...args) { super(...args); window.__testSocket = this; }
                };
                document.addEventListener("DOMContentLoaded", () => {
                    document.getElementById("components-reconnect-modal")
                        ?.addEventListener("components-reconnect-state-changed", event => {
                            document.documentElement.dataset.reconnectState = event.detail.state;
                            if (event.detail.state === "show")
                                document.documentElement.dataset.reconnectStarted = "true";
                        });
                }, { once: true });
                document.addEventListener("securitypolicyviolation", event => {
                    window.__recordCspViolation(event.effectiveDirective);
                });
            """)
            admin = context.new_page()
            guest = context.new_page()
            def watch(page):
                page.on("pageerror", lambda error: errors.append(str(error)))
                page.on("console", lambda message: errors.append(message.text)
                        if message.type == "error" and not message.location.get("url", "").endswith("/favicon.ico")
                        else None)
            watch(admin)
            watch(guest)
            stage = "head-mutation-during-invite-render"
            install_transient_head_mutation(guest)
            guest.goto(settings["guestOrigin"] + "/join/invalid")
            expect(guest.locator("html")).to_have_attribute("data-head-mutation", "complete")
            expect(guest.get_by_test_id("guest-invalid")).to_contain_text("This invitation is invalid")
            expect(guest.get_by_role("heading", name="INVALID INVITE.")).to_have_count(0)
            expect(guest.get_by_text("Guest controls", exact=True)).to_have_count(0)
            expect(guest.get_by_text("Connection lost. Open the invitation link again.", exact=True)).to_have_count(0)
            expect(guest.locator("[data-input]")).to_have_count(0)
            expect(guest).to_have_title("Guest controls")
            expect(guest.locator("head title")).to_have_count(1)
            assert_top_right(guest, guest.get_by_test_id("guest-invalid"))
            assert_connection_notices(guest)
            assert_mobile_layout(guest)
            stage = "interactive-title-after-head-mutation"
            guest.evaluate("Blazor.navigateTo('/')")
            expect(guest.locator(".rw-operation-notice")).to_contain_text("Open a valid invitation from the operator.")
            expect(guest).to_have_title("Invite required")
            expect(guest.locator("head title")).to_have_count(1)
            assert guest.evaluate("window.__headMutation.removed && window.__headMutation.restored")
            assert not errors, "browser-error"
            assert not csp_violations, "content-security-policy-violation"

            stage = "operator-page"
            admin.goto(settings["adminUrl"])
            expect(admin.get_by_role("button", name="Stop", exact=True)).to_have_count(0)
            end = admin.get_by_role("button", name="Stop and end session", exact=True)
            expect(end.locator("..")).to_have_attribute("variant", "warning")
            expect(admin.get_by_test_id("session-mode")).to_have_text("No guest")
            expect(admin.get_by_text("Applying limits pauses control.", exact=True)).to_have_count(0)
            expect(admin.get_by_text("Share this link with someone.", exact=True)).to_be_visible()
            assert_connection_notices(admin)
            assert_button_accessibility(admin)
            assert_mobile_layout(admin)
            stage = "automatic-invitation"
            expect(admin.get_by_role("button", name="Create invite", exact=True)).to_have_count(0)
            cycle = admin.get_by_role("button", name="Cycle link", exact=True)
            expect(cycle).to_be_enabled()
            expect(cycle).to_have_text("")
            expect(cycle).to_have_attribute("title", "Cycle link")
            expect(cycle.locator("svg[aria-hidden='true']")).to_be_visible()
            cycle_size = cycle.bounding_box()
            assert cycle_size and 24 <= cycle_size["width"] <= 44
            assert abs(cycle_size["width"] - cycle_size["height"]) < 1
            assert cycle.evaluate("element => getComputedStyle(element).borderRadius") == "50%"
            link = admin.get_by_test_id("invitation-link")
            expect(link).to_be_visible()
            invitation = link.input_value()
            invitations.append(invitation)
            assert invitation.startswith(settings["guestOrigin"] + "/join/")
            copy = admin.get_by_role("button", name="Copy", exact=True)
            assert abs(link.bounding_box()["height"] - copy.bounding_box()["height"]) < 1
            stage = "copy-success-style"
            admin.evaluate("""() => {
                window.__copiedInvitation = null;
                Object.defineProperty(navigator.clipboard, "writeText", {
                    configurable: true,
                    value: async value => { window.__copiedInvitation = value; }
                });
            }""")
            copy.click()
            copied = admin.get_by_role("button", name="Copied", exact=True)
            expect(copied.locator("..")).to_have_attribute("variant", "success")
            assert admin.evaluate("window.__copiedInvitation") == invitation
            expect(copy).to_be_visible(timeout=4000)
            expect(copy.locator("..")).to_have_attribute("variant", "secondary")
            stage = "draft-limit-markers"
            cap = admin.get_by_label("Speed cap (%)", exact=True)
            applied_cap = cap.input_value()
            cap.fill("35")
            expect(admin.get_by_test_id("limits-dirty")).to_be_visible()
            expect(cap.locator("..")).to_have_attribute("data-applied-value", applied_cap)
            expect(cap.locator("..").locator(":scope > svg.rw-range-marker")).to_be_visible()
            dirty = admin.get_by_test_id("limits-dirty").bounding_box()
            heading = admin.get_by_role("heading", name="Session limits", exact=True).bounding_box()
            assert dirty and heading and dirty["x"] > heading["x"]
            assert abs(dirty["y"] - heading["y"]) < 3
            cap.fill(applied_cap)
            expect(admin.get_by_test_id("limits-dirty")).to_have_count(0)
            stage = "operator-reload-preserves-invitation"
            admin.reload()
            expect(link).to_have_value(invitation)
            stage = "guest-join"
            guest.goto(invitation)
            expect(guest.locator("html")).to_have_attribute("data-head-mutation", "complete")
            expect(guest.locator("[data-input]")).to_have_count(3)
            expect(guest.locator(".guest-session > rw-alert")).to_contain_text("Waiting for the operator.")
            expect(admin.get_by_test_id("session-mode")).to_have_text("Guest waiting")
            expect(admin.get_by_test_id("session-mode")).to_have_attribute("variant", "warning")
            allow = admin.get_by_role("button", name="Allow control", exact=True)
            expect(allow).to_be_enabled()
            expect(allow.locator("..")).to_have_attribute("variant", "success")
            expect(allow).to_have_class(re.compile(r"\brw-attention\b"))
            assert allow.evaluate("element => getComputedStyle(element).animationName") != "none"
            admin.emulate_media(reduced_motion="reduce")
            assert allow.evaluate("element => getComputedStyle(element).animationName") == "none"
            admin.emulate_media(reduced_motion="no-preference")
            assert_button_accessibility(admin)
            assert_button_accessibility(guest)
            assert abs(admin.locator("main").bounding_box()["width"] - guest.locator("main").bounding_box()["width"]) < 1
            assert_mobile_layout(guest)
            assert not urlsplit(guest.url).fragment
            assert not errors, "browser-error"
            assert not csp_violations, "content-security-policy-violation"
            stage = "reject-second-active-guest"
            second = context.new_page()
            watch(second)
            second.goto(invitation)
            expect(second.get_by_test_id("guest-invalid")).to_be_visible()
            expect(second.locator("[data-input]")).to_have_count(0)
            second.close()
            stage = "allow-control"
            allow = admin.get_by_role("button", name="Allow control", exact=True)
            expect(allow).to_be_enabled()
            allow.click()
            guest.bring_to_front()
            expect(guest.get_by_role("button", name="Send", exact=True)).to_be_enabled()
            expect(admin.get_by_test_id("session-mode")).to_have_text("Guest connected")
            expect(admin.get_by_test_id("session-mode")).to_have_attribute("variant", "success")
            stage = "limits-without-pausing"
            cap.fill("90")
            admin.get_by_role("button", name="Apply limits", exact=True).click()
            expect(admin.get_by_test_id("limits-dirty")).to_have_count(0)
            expect(admin.get_by_test_id("controller-mode")).to_have_text("Control enabled")
            expect(guest.get_by_role("button", name="Send", exact=True)).to_be_enabled()
            stage = "selected-settings"
            speed = guest.get_by_label("Speed", exact=True)
            speed.focus()
            speed.press("Home")
            speed.press("ArrowUp")
            expect(guest.locator('[data-value="speed"]')).to_have_text("1")
            stage = "live-limits-during-renewal"
            guest.get_by_role("button", name="Send", exact=True).click()
            expect(guest.locator('[data-sent="speed"]')).to_have_text("1")
            cap.fill("85")
            admin.get_by_role("button", name="Apply limits", exact=True).click()
            expect(admin.get_by_test_id("limits-dirty")).to_have_count(0)
            expect(admin.get_by_test_id("controller-mode")).to_have_text("Control enabled")
            expect(guest.get_by_role("button", name="Send", exact=True)).to_be_enabled()
            expect(guest.locator('[data-sent="speed"]')).to_have_text("1")
            stage = "stop"
            guest.get_by_role("button", name="Stop", exact=True).click()
            expect(guest.get_by_role("button", name="Send", exact=True)).to_be_disabled()
            stage = "guest-leave"
            guest.get_by_role("button", name="Leave session", exact=True).click()
            expect(guest.get_by_test_id("guest-ended")).to_be_visible()
            stage = "same-link-after-guest-leaves"
            expect(link).to_have_value(invitation)
            guest.close()
            guest = context.new_page()
            watch(guest)
            guest.goto(invitation)
            expect(guest.locator(".guest-session > rw-alert")).to_contain_text("Waiting for the operator.")
            expect(guest.get_by_role("button", name="Send", exact=True)).to_be_disabled()
            stage = "end-session"
            end.click()
            expect(guest.get_by_test_id("guest-ended")).to_be_visible()
            expect(link).not_to_have_value(invitation)
            ended_invitation = invitation
            invitation = link.input_value()
            invitations.append(invitation)
            stage = "operator-end-replaces-link"
            guest.close()
            guest = context.new_page()
            watch(guest)
            guest.goto(ended_invitation)
            expect(guest.get_by_test_id("guest-invalid")).to_be_visible()
            guest.close()
            guest = context.new_page()
            watch(guest)
            guest.goto(invitation)
            expect(guest.locator(".guest-session > rw-alert")).to_contain_text("Waiting for the operator.")
            expect(guest.get_by_role("button", name="Send", exact=True)).to_be_disabled()
            expect(allow).to_be_enabled()
            allow.click()
            guest.bring_to_front()
            expect(guest.get_by_role("button", name="Send", exact=True)).to_be_enabled()
            stage = "cycle-revokes-active-guest"
            expect(cycle).to_be_enabled()
            cycle.click()
            expect(link).not_to_have_value(invitation)
            replacement = link.input_value()
            invitations.append(replacement)
            expect(guest.get_by_test_id("guest-ended")).to_be_visible()
            stage = "cycled-link-is-invalid"
            guest.close()
            guest = context.new_page()
            watch(guest)
            guest.goto(invitation)
            expect(guest.get_by_test_id("guest-invalid")).to_be_visible()
            expect(guest.locator("[data-input]")).to_have_count(0)
            stage = "replacement-link-needs-approval"
            guest.close()
            guest = context.new_page()
            watch(guest)
            guest.goto(replacement)
            expect(guest.locator(".guest-session > rw-alert")).to_contain_text("Waiting for the operator.")
            expect(guest.get_by_role("button", name="Send", exact=True)).to_be_disabled()
            stage = "guest-transport-loss-fails-closed"
            assert_transport_loss(guest, guest=True)
            expect(admin.get_by_test_id("session-mode")).to_have_text("No guest")
            expect(link).to_have_value(replacement)
            stage = "operator-transport-recovery"
            assert_transport_loss(admin, guest=False)
            expect(admin.get_by_test_id("controller-mode")).to_have_text("Paused")
            stage = "reload-without-invitation"
            guest.reload()
            expect(guest.get_by_test_id("guest-invalid")).to_be_visible()
            assert not errors, "browser-error"
            assert not csp_violations, "content-security-policy-violation"
            browser.close()
        print("Head mutation, shared responsive layouts and notices, accessible button states, Copy feedback, applied-limit markers, live non-pausing limits, reusable invitations, operator End rotation, cycle revocation, fresh approval and Stop passed.")
        return 0
    except Exception as error:
        # Call logs can contain capability URLs. Never include the call log in output.
        detail = str(error).split("Call log:")[0]
        for secret_link in invitations:
            detail = detail.replace(secret_link, "[invitation]")
        print(json.dumps({"stage": stage, "error": type(error).__name__,
                          "location": [f"{frame.name}:{frame.lineno}" for frame in traceback.extract_tb(error.__traceback__)
                                       if frame.filename == __file__],
                          "detail": detail[:500], "scriptErrors": len(errors),
                          "cspViolations": csp_violations,
                          "diagnostics": [re.sub(r"https?://[^\s\"'<>]+", "[url]", error)[:1200]
                                          for error in errors[:3]]}))
        return 1


if __name__ == "__main__":
    sys.exit(main())
