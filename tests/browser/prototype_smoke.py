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
    buttons = page.get_by_role("button").evaluate_all("""elements => elements.map(element => ({
        ariaLabel: element.getAttribute("aria-label"),
        text: element.innerText.trim(),
        title: element.getAttribute("title"),
        disabled: element.disabled,
        filter: getComputedStyle(element).filter
    }))""")
    for button in buttons:
        assert button["ariaLabel"] or button["text"], "button-accessible-name"
        assert button["title"], "button-tooltip"
        if button["disabled"]:
            assert button["filter"] == "grayscale(1)"


def assert_top_right(page, notice):
    expect(notice).to_be_visible()
    box = notice.bounding_box()
    assert box and abs(box["y"] - 16) < 1
    assert abs(page.viewport_size["width"] - box["x"] - box["width"] - 16) < 1


def assert_inline_notification(page, notification):
    expect(notification).to_be_visible()
    notification_bounds = notification.bounding_box()
    main = page.locator("main")
    main_bounds = main.bounding_box()
    padding = main.evaluate("""element => {
        const style = getComputedStyle(element);
        return { left: parseFloat(style.paddingLeft), right: parseFloat(style.paddingRight) };
    }""")
    assert notification_bounds and main_bounds
    assert abs(notification_bounds["x"] - main_bounds["x"] - padding["left"]) < 1
    assert abs(notification_bounds["width"] - main_bounds["width"] + padding["left"] + padding["right"]) < 1
    assert notification.evaluate("element => getComputedStyle(element).position") == "static"
    assert notification.evaluate("element => getComputedStyle(element).borderTopWidth") == "0px"
    expect(notification).to_have_class(re.compile(r"\brw-inline-notification\b"))


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


def assert_page_title_layout(page, title):
    header = page.locator(".rw-page-header")
    expect(header).to_contain_text(title)
    header_bounds = header.bounding_box()
    page_bounds = page.locator("main").bounding_box()
    assert header_bounds and page_bounds and abs(header_bounds["x"] - page_bounds["x"]) < 1
    assert page_bounds["y"] - header_bounds["y"] - header_bounds["height"] >= 16


def assert_operator_layout(page):
    feedback = page.locator(".feedback-disclosure")
    expect(feedback.get_by_test_id("live-feedback")).to_have_text("Live feedback")
    expect(feedback).not_to_contain_text("Connected")

    controls = page.get_by_test_id("operator-controls")
    invitation = page.get_by_test_id("guest-invitation-card")
    limits = page.get_by_test_id("session-limits")
    controls_bounds = controls.bounding_box()
    invitation_bounds = invitation.bounding_box()
    limits_bounds = limits.bounding_box()
    assert controls_bounds and invitation_bounds and limits_bounds
    assert abs(controls_bounds["y"] - invitation_bounds["y"]) < 1
    assert abs(controls_bounds["width"] - invitation_bounds["width"]) < 1
    assert abs(controls_bounds["height"] - invitation_bounds["height"]) < 1
    assert abs(limits_bounds["x"] - controls_bounds["x"]) < 1
    assert abs(limits_bounds["x"] + limits_bounds["width"] - invitation_bounds["x"] - invitation_bounds["width"]) < 1

    controls_heading = page.get_by_test_id("controller-mode")
    limits_heading = page.get_by_role("heading", name="Session limits", exact=True)
    expect(controls_heading).to_have_text("Controls")
    controls_heading_bounds = controls_heading.bounding_box()
    limits_heading_bounds = limits_heading.bounding_box()
    assert controls_heading_bounds and limits_heading_bounds
    assert limits_heading_bounds["height"] < controls_heading_bounds["height"]

    badges = page.get_by_test_id("operator-status-badges")
    simulator_bounds = page.get_by_test_id("simulator-mode").bounding_box()
    session_bounds = page.get_by_test_id("session-mode").bounding_box()
    badges_bounds = badges.bounding_box()
    assert simulator_bounds and session_bounds and badges_bounds
    assert abs(simulator_bounds["y"] - session_bounds["y"]) < 1
    assert simulator_bounds["x"] + simulator_bounds["width"] <= session_bounds["x"]
    assert abs(badges_bounds["x"] + badges_bounds["width"] - session_bounds["x"] - session_bounds["width"]) < 1
    assert badges_bounds["x"] > controls_heading_bounds["x"]

    limits_controls = page.get_by_test_id("limits-controls")
    apply_bounds = page.get_by_role("button", name="Apply limits", exact=True).bounding_box()
    cap_bounds = page.get_by_label("Speed cap (%)", exact=True).bounding_box()
    limits_controls_bounds = limits_controls.bounding_box()
    assert apply_bounds and cap_bounds and limits_controls_bounds
    assert apply_bounds["y"] < cap_bounds["y"] + cap_bounds["height"]
    assert apply_bounds["y"] + apply_bounds["height"] > cap_bounds["y"]
    assert abs(apply_bounds["x"] + apply_bounds["width"] - limits_controls_bounds["x"] - limits_controls_bounds["width"]) < 1
    expect(page.locator(".rw-range-values")).to_have_count(0)


def assert_transport_loss(page, guest):
    assert page.evaluate("window.__testSocket?.readyState") == 1
    page.evaluate("window.__testSocket.close()")
    expect(page.locator("html")).to_have_attribute("data-reconnect-started", "true")
    notice = page.locator("#components-reconnect-modal")
    if guest:
        disconnected = page.get_by_test_id("guest-disconnected")
        expect(disconnected).to_contain_text("Connection lost. Open the invitation link again.")
        expect(disconnected).to_have_attribute("variant", "danger")
        assert_inline_notification(page, disconnected)
        expect(page.locator(".guest-session-message")).to_be_hidden()
        guest_interface = page.locator(".guest-interface")
        if guest_interface.count():
            assert guest_interface.evaluate("element => getComputedStyle(element).filter") == "grayscale(1)"
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
            if settings.get("scope") == "operator-layout":
                stage = "operator-layout"
                admin.goto(settings["adminUrl"])
                expect(admin.get_by_test_id("session-mode")).to_have_text("No guest")
                assert_page_title_layout(admin, "Operator controls")
                assert_operator_layout(admin)
                expect(admin.get_by_text("Applying limits pauses control.", exact=True)).to_have_count(0)
                expect(admin.get_by_text("Share this link with the guest.", exact=True)).to_be_visible()
                assert_connection_notices(admin)
                assert_button_accessibility(admin)
                assert_mobile_layout(admin)
                assert not errors, "browser-error"
                assert not csp_violations, "content-security-policy-violation"
                browser.close()
                print("Operator layout checks passed.")
                return 0

            if settings.get("scope") == "guest-approval-notice":
                stage = "guest-approval-notice"
                admin.goto(settings["adminUrl"])
                invitation = admin.get_by_test_id("invitation-link").input_value()
                guest.goto(invitation)
                expect(guest.locator(".guest-session-message")).to_contain_text("Waiting for the operator.")
                expect(admin.get_by_test_id("session-mode")).to_have_text("Guest waiting")

                pending_notice = admin.get_by_test_id("pending-guest-notice")
                expect(pending_notice).to_contain_text("A guest is waiting. Click Allow control to enable control.")
                expect(pending_notice).to_have_attribute("variant", "warning")
                assert_inline_notification(admin, pending_notice)

                allow = admin.get_by_role("button", name="Allow control", exact=True)
                expect(allow.locator("..")).to_have_attribute("variant", "success")
                expect(allow).to_have_class(re.compile(r"\brw-attention\b"))
                assert allow.evaluate("element => getComputedStyle(element).animationName") != "none"
                assert "linear-gradient" in allow.evaluate("element => getComputedStyle(element).backgroundImage")
                assert allow.evaluate("element => getComputedStyle(element).boxShadow") != "none"
                admin.emulate_media(reduced_motion="reduce")
                assert allow.evaluate("element => getComputedStyle(element).animationName") == "none"
                admin.emulate_media(reduced_motion="no-preference")

                allow.click()
                expect(admin.get_by_test_id("session-mode")).to_have_text("Guest connected")
                expect(admin.get_by_test_id("pending-guest-notice")).to_have_count(0)
                assert not errors, "browser-error"
                assert not csp_violations, "content-security-policy-violation"
                browser.close()
                print("Guest approval notice checks passed.")
                return 0

            if settings.get("scope") == "shared-notification-template":
                stage = "invite-required-notification"
                guest.goto(settings["guestOrigin"] + "/join/invalid")
                expect(guest.get_by_test_id("guest-invalid")).to_contain_text("This invitation is invalid")
                assert_page_title_layout(guest, "Guest controls")
                assert_inline_notification(guest, guest.get_by_test_id("guest-invalid"))

                stage = "operator-pending-notification"
                admin.goto(settings["adminUrl"])
                assert_page_title_layout(admin, "Operator controls")
                invitation = admin.get_by_test_id("invitation-link").input_value()
                guest.goto(invitation)
                expect(guest.locator(".guest-session-message")).to_contain_text("Waiting for the operator.")
                assert_inline_notification(guest, guest.locator(".guest-session-message"))
                expect(admin.get_by_test_id("session-mode")).to_have_text("Guest waiting")
                pending_notice = admin.get_by_test_id("pending-guest-notice")
                expect(pending_notice).to_contain_text("A guest is waiting. Click Allow control to enable control.")
                assert_inline_notification(admin, pending_notice)

                allow = admin.get_by_role("button", name="Allow control", exact=True)
                expect(allow).to_be_enabled()
                expect(allow).to_have_class(re.compile(r"\brw-attention\b"))
                assert allow.evaluate("element => getComputedStyle(element).animationName") != "none"
                assert "linear-gradient" in allow.evaluate("element => getComputedStyle(element).backgroundImage")
                admin.emulate_media(reduced_motion="reduce")
                assert admin.get_by_role("button", name="Allow control", exact=True).evaluate(
                    "element => getComputedStyle(element).animationName") == "none"
                admin.emulate_media(reduced_motion="no-preference")

                stage = "guest-session-ended-notification"
                allow.click()
                expect(guest.get_by_role("button", name="Send", exact=True)).to_be_enabled()
                guest.get_by_role("button", name="Leave session", exact=True).click()
                ended = guest.get_by_test_id("guest-ended")
                expect(ended).to_contain_text("Session ended. Open the invite link again to rejoin.")
                assert_page_title_layout(guest, "Guest controls")
                assert_inline_notification(guest, ended)
                assert not errors, "browser-error"
                assert not csp_violations, "content-security-policy-violation"
                browser.close()
                print("Shared notification template checks passed.")
                return 0

            stage = "head-mutation-during-invite-render"
            install_transient_head_mutation(guest)
            guest.goto(settings["guestOrigin"] + "/join/invalid")
            expect(guest.locator("html")).to_have_attribute("data-head-mutation", "complete")
            expect(guest.get_by_test_id("guest-invalid")).to_contain_text("This invitation is invalid")
            expect(guest.get_by_role("heading", name="INVALID INVITE.")).to_have_count(0)
            assert_page_title_layout(guest, "Guest controls")
            expect(guest.get_by_text("Connection lost. Open the invitation link again.", exact=True)).to_have_count(0)
            expect(guest.locator("[data-input]")).to_have_count(0)
            expect(guest).to_have_title("Guest controls")
            expect(guest.locator("head title")).to_have_count(1)
            assert_inline_notification(guest, guest.get_by_test_id("guest-invalid"))
            assert_connection_notices(guest)
            assert_mobile_layout(guest)
            stage = "interactive-title-after-head-mutation"
            guest.evaluate("Blazor.navigateTo('/')")
            invite_required = guest.locator(".rw-inline-notification")
            expect(invite_required).to_contain_text("Open a valid invitation from the operator.")
            assert_inline_notification(guest, invite_required)
            expect(guest).to_have_title("Invite required")
            expect(guest.locator("head title")).to_have_count(1)
            assert guest.evaluate("window.__headMutation.removed && window.__headMutation.restored")
            assert not errors, "browser-error"
            assert not csp_violations, "content-security-policy-violation"

            stage = "operator-page"
            admin.goto(settings["adminUrl"])
            expect(admin.get_by_test_id("session-mode")).to_have_text("No guest")
            assert_page_title_layout(admin, "Operator controls")
            expect(admin.get_by_role("button", name="Stop", exact=True)).to_have_count(0)
            end = admin.get_by_role("button", name="Stop and end session", exact=True)
            expect(end.locator("..")).to_have_attribute("variant", "warning")
            assert_operator_layout(admin)
            simulator_mode = admin.get_by_test_id("simulator-mode")
            expect(simulator_mode).to_have_text("SIMULATOR")
            simulator_bounds = simulator_mode.bounding_box()
            session_bounds = admin.get_by_test_id("session-mode").bounding_box()
            assert simulator_bounds and session_bounds and simulator_bounds["x"] + simulator_bounds["width"] <= session_bounds["x"]
            expect(admin.get_by_text("Applying limits pauses control.", exact=True)).to_have_count(0)
            expect(admin.get_by_text("Share this link with the guest.", exact=True)).to_be_visible()
            assert_connection_notices(admin)
            assert_button_accessibility(admin)
            assert_mobile_layout(admin)
            stage = "automatic-invitation"
            expect(admin.get_by_role("button", name="Create invite", exact=True)).to_have_count(0)
            cycle = admin.get_by_role("button", name="Replace invitation link", exact=True)
            expect(cycle).to_be_enabled()
            expect(cycle).to_have_text("")
            expect(cycle).to_have_attribute(
                "title", "Replace the invitation link, revoke the current guest, and request Stop"
            )
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
            assert cap.locator("..").get_attribute("data-applied-value") is None
            expect(cap.locator("..").locator(":scope > svg.rw-range-marker")).to_have_count(0)
            stage = "operator-reload-preserves-invitation"
            admin.reload()
            expect(link).to_have_value(invitation)
            stage = "guest-join"
            guest.goto(invitation)
            expect(guest.locator("html")).to_have_attribute("data-head-mutation", "complete")
            expect(guest.locator("[data-input]")).to_have_count(3)
            expect(guest.locator(".guest-session-message")).to_contain_text("Waiting for the operator.")
            expect(guest.locator(".guest-session-message")).to_have_attribute("variant", "warning")
            expect(admin.get_by_test_id("session-mode")).to_have_text("Guest waiting")
            expect(admin.get_by_test_id("session-mode")).to_have_attribute("variant", "warning")
            pending_notice = admin.get_by_test_id("pending-guest-notice")
            expect(pending_notice).to_contain_text("A guest is waiting. Click Allow control to enable control.")
            expect(pending_notice).to_have_attribute("variant", "warning")
            assert_inline_notification(admin, pending_notice)
            monitor = guest.locator(".motion-monitor")
            expect(monitor.get_by_text("Live Status", exact=True)).to_be_visible()
            expect(monitor.get_by_text("Live feedback", exact=True)).to_have_count(0)
            expect(monitor.get_by_test_id("simulator-mode")).to_have_text("SIMULATOR")
            expect(monitor.get_by_test_id("feedback-mode")).to_have_text("Connected")
            expect(monitor.get_by_test_id("live-position")).to_have_count(0)
            expect(monitor.get_by_test_id("live-velocity")).to_have_count(0)
            expect(monitor.get_by_text("Position", exact=True)).to_have_count(0)
            expect(monitor.locator(".motion-rail")).to_be_visible()
            simulator_bounds = monitor.get_by_test_id("simulator-mode").bounding_box()
            feedback_bounds = monitor.get_by_test_id("feedback-mode").bounding_box()
            heading_bounds = monitor.locator(".monitor-heading").bounding_box()
            assert simulator_bounds and feedback_bounds and heading_bounds
            assert abs(simulator_bounds["y"] - feedback_bounds["y"]) < 1
            assert simulator_bounds["x"] + simulator_bounds["width"] <= feedback_bounds["x"]
            assert abs(heading_bounds["x"] + heading_bounds["width"] - feedback_bounds["x"] - feedback_bounds["width"]) < 1
            assert guest.locator("main").evaluate("element => getComputedStyle(element).outlineStyle") == "none"
            send = guest.get_by_role("button", name="Send", exact=True)
            auto_bounds = guest.locator(".auto-send").bounding_box()
            send_bounds = send.bounding_box()
            stop_bounds = guest.get_by_role("button", name="Stop", exact=True).bounding_box()
            assert auto_bounds and send_bounds and stop_bounds
            assert auto_bounds["x"] + auto_bounds["width"] <= send_bounds["x"]
            assert stop_bounds["x"] - send_bounds["x"] - send_bounds["width"] > send_bounds["x"] - auto_bounds["x"] - auto_bounds["width"]
            expect(send.locator("..")).to_have_attribute("variant", "success")
            expect(guest.locator("[data-send-status]")).to_have_count(0)
            allow = admin.get_by_role("button", name="Allow control", exact=True)
            expect(allow).to_be_enabled()
            expect(allow.locator("..")).to_have_attribute("variant", "success")
            expect(allow).to_have_class(re.compile(r"\brw-attention\b"))
            assert allow.evaluate("element => getComputedStyle(element).animationName") != "none"
            assert "linear-gradient" in allow.evaluate("element => getComputedStyle(element).backgroundImage")
            assert allow.evaluate("element => getComputedStyle(element).boxShadow") != "none"
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
            expect(admin.get_by_test_id("pending-guest-notice")).to_have_count(0)
            expect(guest.locator(".guest-session-message")).to_contain_text("Control enabled.")
            expect(guest.locator(".guest-session-message")).to_have_attribute("variant", "success")
            stage = "limits-without-pausing"
            cap.fill("90")
            admin.get_by_role("button", name="Apply limits", exact=True).click()
            expect(admin.get_by_test_id("limits-dirty")).to_have_count(0)
            expect(admin.get_by_test_id("controller-mode")).to_have_text("Controls")
            assert cap.locator("..").get_attribute("data-applied-value") is None
            expect(cap.locator("..").locator(":scope > svg.rw-range-marker")).to_have_count(0)
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
            expect(admin.get_by_test_id("controller-mode")).to_have_text("Controls")
            assert cap.locator("..").get_attribute("data-applied-value") is None
            expect(cap.locator("..").locator(":scope > svg.rw-range-marker")).to_have_count(0)
            expect(guest.get_by_role("button", name="Send", exact=True)).to_be_enabled()
            expect(guest.locator('[data-sent="speed"]')).to_have_text("1")
            stage = "stop"
            guest.get_by_role("button", name="Stop", exact=True).click()
            expect(guest.locator(".guest-session-message")).to_contain_text("Paused. Waiting for the operator.")
            expect(guest.locator(".guest-session-message")).to_have_attribute("variant", "warning")
            expect(guest.get_by_role("button", name="Send", exact=True)).to_be_disabled()
            expect(guest.locator("[data-send-status]")).to_have_count(0)
            stage = "guest-leave"
            guest.get_by_role("button", name="Leave session", exact=True).click()
            expect(guest.get_by_test_id("guest-ended")).to_contain_text("Session ended. Open the invite link again to rejoin.")
            assert_inline_notification(guest, guest.get_by_test_id("guest-ended"))
            stage = "same-link-after-guest-leaves"
            expect(link).to_have_value(invitation)
            guest.close()
            guest = context.new_page()
            watch(guest)
            guest.goto(invitation)
            expect(guest.locator(".guest-session-message")).to_contain_text("Waiting for the operator.")
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
            expect(guest.locator(".guest-session-message")).to_contain_text("Waiting for the operator.")
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
            expect(guest.locator(".guest-session-message")).to_contain_text("Waiting for the operator.")
            expect(guest.get_by_role("button", name="Send", exact=True)).to_be_disabled()
            stage = "guest-transport-loss-fails-closed"
            assert_transport_loss(guest, guest=True)
            expect(admin.get_by_test_id("session-mode")).to_have_text("No guest")
            expect(link).to_have_value(replacement)
            stage = "operator-transport-recovery"
            assert_transport_loss(admin, guest=False)
            expect(admin.get_by_test_id("controller-mode")).to_have_text("Controls")
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
