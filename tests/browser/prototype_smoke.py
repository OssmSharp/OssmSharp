import json
import sys
from pathlib import Path
from urllib.parse import urlsplit

from playwright.sync_api import expect, sync_playwright


def main():
    settings = json.loads(sys.stdin.readline())
    stage = "starting-browser"
    errors = []
    try:
        with sync_playwright() as playwright:
            browser = playwright.chromium.launch(channel="msedge", headless=True)
            admin_context = browser.new_context(viewport={"width": 1280, "height": 1000})
            guest_context = browser.new_context(viewport={"width": 1100, "height": 900})
            admin = admin_context.new_page()
            guest = guest_context.new_page()
            for page in (admin, guest):
                page.on("pageerror", lambda error: errors.append(type(error).__name__))
            stage = "operator-bootstrap"
            admin.goto(settings["adminUrl"])
            mode = admin.get_by_test_id("controller-mode")
            expect(mode).to_be_visible()
            assert not urlsplit(admin.url).fragment
            admin.get_by_role("button", name="Initialize / recover").click()
            expect(mode).to_have_text("Paused")
            admin.get_by_label("Speed cap (%)").fill("20")
            admin.get_by_label("Maximum depth (%)").fill("60")
            admin.get_by_role("button", name="Apply limits").click()
            stage = "creating-invitation"
            admin.get_by_role("button", name="Generate session link").click()
            link = admin.get_by_test_id("invitation-link")
            expect(link).to_be_visible()
            invitation = link.input_value()
            assert invitation.startswith(settings["guestOrigin"] + "/join/")
            stage = "guest-join"
            guest.goto(invitation)
            join = guest.get_by_role("button", name="Join simulator session")
            expect(join).to_be_enabled()
            assert not urlsplit(guest.url).fragment
            join.click()
            expect(admin.get_by_test_id("session-mode")).to_have_text("PendingApproval")
            stage = "approve-and-arm"
            admin.get_by_role("button", name="Approve pending guest").click()
            arm = admin.get_by_role("button", name="Arm approved session")
            expect(arm).to_be_enabled()
            arm.click()
            expect(mode).to_have_text("Ready")
            guest.bring_to_front()
            hold = guest.get_by_role("button", name="Hold to send to simulator")
            expect(hold).to_be_enabled()
            stage = "live-hold-and-release"
            speed = guest.get_by_label("Speed", exact=True)
            speed.focus()
            speed.press("Home")
            for _ in range(12):
                speed.press("ArrowRight")
            hold.focus()
            guest.keyboard.down("Space")
            expect(guest.locator(".accepted-values")).to_contain_text("Speed 12")
            guest.keyboard.up("Space")
            expect(mode).to_have_text("Paused")
            expect(hold).to_be_disabled()
            stage = "external-lock-and-unlock"
            admin.bring_to_front()
            admin.get_by_role("button", name="External depth change").click()
            expect(mode).to_have_text("Locked")
            unlock = admin.get_by_role("button", name="Unlock into paused")
            expect(unlock).to_be_enabled()
            unlock.click()
            expect(mode).to_have_text("Paused")
            expect(hold).to_be_disabled()
            stage = "ending-session"
            admin.get_by_role("button", name="End session").click()
            expect(admin.get_by_test_id("session-mode")).to_have_text("Ended")
            expect(guest.get_by_role("heading", name="Session ended or disconnected")).to_be_visible()
            expect(link).not_to_be_visible()
            stage = "responsive-layout"
            guest.set_viewport_size({"width": 390, "height": 844})
            assert guest.evaluate(
                "document.documentElement.scrollWidth <= window.innerWidth"
            )
            admin.set_viewport_size({"width": 390, "height": 844})
            assert admin.evaluate(
                "document.documentElement.scrollWidth <= window.innerWidth"
            )
            destination = Path(".artifacts/browser")
            destination.mkdir(parents=True, exist_ok=True)
            admin.screenshot(path=str(destination / "admin.png"), full_page=True)
            guest.screenshot(path=str(destination / "guest.png"), full_page=True)
            assert not errors, "browser-script-error"
            browser.close()
        print("Blazor bootstrap, invitation, hold/release, external lock/unlock and mobile layout passed.")
        return 0
    except Exception as error:
        # Never print Playwright call logs: a navigation failure may contain an invitation URL.
        print(json.dumps({"stage": stage, "error": type(error).__name__, "scriptErrors": len(errors)}))
        return 1


if __name__ == "__main__":
    sys.exit(main())
