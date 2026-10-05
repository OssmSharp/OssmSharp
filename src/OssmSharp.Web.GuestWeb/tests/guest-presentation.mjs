import assert from "node:assert/strict";
import { after, before, test } from "node:test";
import {
  button, complete, createBrowserFixture, deviceState, guestState, injectFatal,
  launchBrowser, preview, sent, setRange, settings, until, userState,
} from "./fixtures/browser-fixture.mjs";

let browser;
before(async () => { browser = await launchBrowser(); }, { timeout: 20_000 });
after(async () => { await browser?.close(); }, { timeout: 20_000 });
const options = { timeout: 60_000 };

for (const viewport of [{ width: 1280, height: 800 }, { width: 390, height: 844 }]) {
  test(`representative controls notices and long metadata avoid horizontal overflow at ${viewport.width}`, options, async (t) => {
    const fixture = await createBrowserFixture(t, browser);
    const admin = await fixture.openAdmin(userState(deviceState({
      name: "Test device with a deliberately long descriptive name",
      identifier: "isolated-device-" + "1234567890".repeat(10),
      connectedAt: new Date().toISOString(), transmittedSettings: settings,
    })));
    const guest = await fixture.openGuest(`overflow-${viewport.width}`);
    for (const view of [admin, guest]) await view.page.setViewportSize(viewport);
    await setRange(guest.page, "Speed", 35);
    await until(() => preview(guest.page).getAttribute("aria-label"), (label) => label.includes("35% speed"), "requested preview");
    await noOverflow(admin.page);
    await noOverflow(guest.page);
    assert.equal(await button(guest.page, "Send").isVisible(), true);
    assert.equal(await guest.page.getByLabel("Auto send", { exact: true }).isVisible(), true);
    assert.equal(await admin.page.getByTestId("device-identifier").innerText(), admin.session.state.device.identifier);
    await injectFatal(admin.page);
    await admin.page.getByRole("alert").filter({ hasText: "Connection lost." }).waitFor();
    await guest.session.api.wire.dispose();
    await guest.page.getByTestId("guest-disconnected").waitFor({ state: "visible" });
    await noOverflow(admin.page);
    await noOverflow(guest.page);
    assert.deepEqual(admin.errors, []);
    assert.deepEqual(guest.errors, []);
  });
}

test("reduced motion suppresses travel while retaining a still preview", options, async (t) => {
  const fixture = await createBrowserFixture(t, browser);
  const { page, errors } = await fixture.openGuest("reduced-motion");
  await setRange(page, "Speed", 35);
  await until(() => preview(page).getAttribute("aria-label"), (label) => label.includes("35% speed"), "active preview");
  await motion(page, true);
  await page.emulateMedia({ reducedMotion: "reduce" });
  assert.equal(await preview(page).isVisible(), true);
  assert.equal(await preview(page).locator(".preview-carriage").isVisible(), false);
  assert.equal(await preview(page).locator(".preview-still").isVisible(), true);
  await motion(page, false);
  await page.emulateMedia({ reducedMotion: "no-preference" });
  await motion(page, true);
  assert.deepEqual(errors, []);
});

test("guest preview motion pauses on safety and loss and resumes only deliberately", options, async (t) => {
  const fixture = await createBrowserFixture(t, browser);
  const { page, session, errors } = await fixture.openGuest("guest-motion");
  await setRange(page, "Speed", 35);
  await motion(page, true);
  await button(page, "Reset Position").click();
  await until(() => button(page, "Send").isDisabled(), true, "reset queued");
  await motion(page, false);
  session.state = guestState({ canResumeAfterStop: true, device: deviceState({ isArmed: false, stroke: 100, depth: 100 }) });
  await complete(session, "GuestResetPosition", { accepted: true, code: "position-reset" });
  await until(() => button(page, "Send").isEnabled(), true, "reset completion");
  await motion(page, false);
  assert.equal(session.requests.length, 0);
  await button(page, "Send").click();
  await sent(page, "Speed", 35);
  await motion(page, true);
  await button(page, "Stop").click();
  await until(() => page.getByLabel("Speed", { exact: true }).inputValue(), "0", "Stop zeroes speed");
  await until(() => button(page, "Send").isEnabled(), true, "Stop acknowledgement");
  await motion(page, false);
  assert.equal(session.requests.length, 1);
  await setRange(page, "Speed", 35);
  await button(page, "Send").click();
  await sent(page, "Speed", 35);
  await motion(page, true);
  session.state.device = null;
  await until(() => button(page, "Send").isDisabled(), true, "device unavailable");
  await motion(page, false);
  assert.equal(await page.getByTestId("guest-disconnected").isVisible(), false);
  session.state = guestState();
  await until(() => button(page, "Send").isEnabled(), true, "device recovered");
  await motion(page, false);
  assert.equal(session.requests.length, 2);
  await setRange(page, "Speed", 35);
  await button(page, "Send").click();
  await sent(page, "Speed", 35);
  await motion(page, true);
  await session.api.wire.dispose();
  await page.getByTestId("guest-disconnected").waitFor({ state: "visible" });
  await motion(page, false);
  assert.deepEqual(errors, []);
});

test("user preview motion pauses on safety and loss and resumes only deliberately", options, async (t) => {
  const fixture = await createBrowserFixture(t, browser);
  const { page, session, errors } = await fixture.openAdmin(userState(deviceState({ transmittedSettings: settings })));
  await motion(page, true);
  await button(page, "Reset Position").click();
  await page.getByRole("status").filter({ hasText: "Resetting position ..." }).waitFor();
  await motion(page, false);
  await complete(session, "ResetPosition", { accepted: true, code: "position-reset" });
  await until(() => button(page, "Reset Position").isEnabled(), true, "reset completion");
  await motion(page, false);
  session.state.device.transmittedSettings = { ...settings, speed: 40 };
  await until(() => preview(page).getAttribute("aria-label"), (label) => label.includes("40% speed"), "changed selection");
  await motion(page, true);
  await button(page, "Stop").click();
  await page.getByRole("status").filter({ hasText: "Stopping ..." }).waitFor();
  await motion(page, false);
  await complete(session, "Stop");
  await until(() => button(page, "Stop").isEnabled(), true, "Stop completion");
  await motion(page, false);
  session.state.device.transmittedSettings = { ...settings, speed: 45 };
  await until(() => preview(page).getAttribute("aria-label"), (label) => label.includes("45% speed"), "new requested selection");
  await motion(page, true);
  const label = await preview(page).getAttribute("aria-label");
  await session.api.wire.dispose();
  await page.getByRole("alert").filter({ hasText: "Unable to connect to the user service." }).waitFor();
  await motion(page, false);
  assert.equal(await preview(page).getAttribute("aria-label"), label);
  assert.deepEqual(errors, []);
  const fatal = await fixture.openAdmin(userState(deviceState({ transmittedSettings: settings })));
  await motion(fatal.page, true);
  const fatalLabel = await preview(fatal.page).getAttribute("aria-label");
  await injectFatal(fatal.page);
  await fatal.page.getByRole("alert").filter({ hasText: "Connection lost." }).waitFor();
  await motion(fatal.page, false);
  assert.equal(await preview(fatal.page).getAttribute("aria-label"), fatalLabel);
  assert.deepEqual(fatal.errors, []);
});

async function noOverflow(page) {
  // Allow real layout/style work to settle before observing viewport overflow.
  await page.evaluate(() => new Promise((resolve) => requestAnimationFrame(() => requestAnimationFrame(resolve))));
  assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
}

async function motion(page, expected) {
  await until(async () => preview(page).evaluate(async (rail) => {
    const carriage = rail.querySelector(".preview-carriage");
    if (!carriage || getComputedStyle(carriage).display === "none" || rail.animationsPaused()) return false;
    // Sample actual animated path activity, not SVG serialization, counts or exact geometry.
    const start = carriage.getPointAtLength(0);
    return new Promise((resolve, reject) => {
      const deadline = setTimeout(() => reject(new Error("Preview animation sampling deadline exceeded")), 2_000);
      let frames = 0;
      function sample() {
        const point = carriage.getPointAtLength(0);
        const moving = Math.abs(point.x - start.x) > .001 || Math.abs(point.y - start.y) > .001;
        if (moving || ++frames >= 20) {
          clearTimeout(deadline);
          resolve(moving);
        } else requestAnimationFrame(sample);
      }
      requestAnimationFrame(sample);
    });
  }), expected, expected ? "actual preview motion" : "actual preview pause");
}
