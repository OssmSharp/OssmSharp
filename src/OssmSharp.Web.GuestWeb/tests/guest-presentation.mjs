import assert from "node:assert/strict";
import { after, before, test } from "node:test";
import {
  button, complete, createBrowserFixture, deviceState, injectFatal,
  launchBrowser, preview, sent, setRange, settings, until, userState,
} from "./fixtures/browser-fixture.mjs";

let browser;
before(async () => { browser = await launchBrowser(); }, { timeout: 20_000 });
after(async () => { await browser?.close(); }, { timeout: 20_000 });
const options = { timeout: 60_000 };

test("controls fit CSS breakpoints with long metadata notices and enlarged text", options, async (t) => {
  const fixture = await createBrowserFixture(t, browser);
  const admin = await fixture.openAdmin(userState(deviceState({
    name: "Test device with a deliberately long descriptive name",
    identifier: "isolated-device-" + "1234567890".repeat(10),
    connectedAt: new Date().toISOString(), transmittedSettings: settings,
  })));
  const { page, session, errors } = await fixture.openGuest("compact-controls");
  await button(page, "Insist").click();
  await setRange(page, "Speed", 57);
  session.state.device = { ...session.state.device, speed: 54.054054054054056, stroke: 37 };
  await until(() => page.getByLabel("Speed", { exact: true }).getAttribute("aria-valuetext"),
    (value) => value.includes("Current value: 54."), "rounded current value");
  for (const viewport of [{ width: 920, height: 600 }, { width: 1001, height: 720 }]) {
    await page.setViewportSize(viewport);
    await desktopFit(page);
    const patterns = page.getByRole("group", { name: "Pattern", exact: true }).getByRole("button");
    assert.equal(await patterns.count(), 7);
    const boxes = await patterns.evaluateAll((buttons) => buttons.map((element) => {
      const { x, y, width, height } = element.getBoundingClientRect();
      return { x, y, width, height };
    }));
    assert.ok(boxes.every((box) => box.height >= 44 && box.x === boxes[0].x));
    assert.ok(boxes.every((box, index) => index === 0 || box.y >= boxes[index - 1].y + boxes[index - 1].height));
    for (const name of ["Send", "Stop", "Reset Position"]) {
      const box = await button(page, name).boundingBox();
      assert.ok(box.y >= 0 && box.y + box.height <= viewport.height, `${name} remains in the viewport`);
    }
  }
  await page.setViewportSize({ width: 920, height: 600 });
  await button(page, "Reset Position").click();
  await complete(session, "GuestResetPosition", { accepted: false, code: "device-io-failed" });
  await page.getByRole("alert").filter({ hasText: "Reset Position failed" }).waitFor();
  await desktopFit(page);
  await button(page, "Stop").click();
  await page.getByRole("alert").filter({ hasText: "Reset Position failed" }).waitFor({ state: "hidden" });
  session.state.controlAllowed = false;
  await page.getByRole("status").filter({ hasText: "Connected. Waiting for the other person to allow control." }).waitFor();
  await desktopFit(page);
  assert.equal(await admin.page.getByTestId("device-identifier").innerText(), admin.session.state.device.identifier);
  await injectFatal(admin.page);
  await admin.page.getByRole("alert").filter({ hasText: "Connection lost." }).waitFor();
  session.holdDisconnectStop = true;
  await session.api.wire.dispose();
  await page.getByTestId("guest-disconnected").waitFor({ state: "visible" });
  for (const width of [1001, 1000, 901, 900, 701, 700, 561, 560, 390]) {
    for (const view of [admin.page, page]) {
      await view.setViewportSize({ width, height: 844 });
      await noOverflow(view);
    }
  }
  await page.setViewportSize({ width: 390, height: 844 });
  for (const view of [admin.page, page]) {
    await view.evaluate(() => {
      const sizes = Array.from(document.querySelectorAll("main, main *"), (element) => [element, parseFloat(getComputedStyle(element).fontSize)]);
      for (const [element, size] of sizes) element.style.fontSize = `${size * 2}px`;
    });
    await noOverflow(view);
  }
  assert.equal(await button(page, "Stop").isVisible(), true);
  assert.deepEqual(admin.errors, []);
  assert.deepEqual(errors, []);
});

test("slider fills and equal Sent Current markers use native thumb travel including endpoints", options, async (t) => {
  const fixture = await createBrowserFixture(t, browser);
  const { page, session, errors } = await fixture.openGuest("slider-geometry");
  await page.setViewportSize({ width: 920, height: 600 });
  for (const [value, expected] of [[12.4, 12], [12.5, 13], [12.6, 13]]) {
    await setRange(page, "Speed", value);
    assert.equal(await page.getByLabel("Speed", { exact: true }).inputValue(), String(expected), "Native step=1 coercion");
    assert.equal(session.requests.length, 0);
  }
  await button(page, "Robo stroke").click();
  await setRange(page, "Depth", 100);
  for (const value of [0, 1, 57, 100]) {
    for (const name of ["Speed", "Stroke", "Sensation"]) await setRange(page, name, value);
    await button(page, "Send").click();
    await sent(page, "Speed", value);
    for (const name of ["speed", "stroke", "sensation"]) {
      await until(() => page.getByLabel(name[0].toUpperCase() + name.slice(1), { exact: true }).getAttribute("aria-valuetext"),
        (text) => text.includes(`Current value: ${value}.`), `equal ${name} current snapshot`);
      const geometry = await page.getByLabel(name[0].toUpperCase() + name.slice(1), { exact: true }).evaluate((input) => {
        const well = input.closest(".slider-well");
        const markers = well.querySelector("svg");
        const sent = well.querySelector(".sent-marker");
        const current = well.querySelector(".current-marker");
        const style = getComputedStyle(input);
        const rectangle = input.getBoundingClientRect();
        const usableHeight = rectangle.height - parseFloat(style.borderTopWidth) - parseFloat(style.borderBottomWidth)
          - parseFloat(getComputedStyle(well).getPropertyValue("--slider-thumb-size"));
        const point = markers.createSVGPoint();
        point.y = sent.y1.baseVal.value;
        return {
          progress: style.getPropertyValue("--range-progress").trim(),
          sent: sent.y1.baseVal.value, current: current.y1.baseVal.value,
          markerHeight: markers.getBoundingClientRect().height, usableHeight,
          markerY: point.matrixTransform(markers.getScreenCTM()).y,
          expectedY: rectangle.y + rectangle.height / 2 + usableHeight * (.5 - Number(input.value) / Number(input.max)),
        };
      });
      assert.equal(geometry.progress, `${value}%`);
      assert.equal(geometry.sent, geometry.current);
      assert.ok(Math.abs(geometry.markerHeight - geometry.usableHeight) <= .5, JSON.stringify(geometry));
      assert.ok(Math.abs(geometry.markerY - geometry.expectedY) <= .5, JSON.stringify(geometry));
    }
  }
  await setRange(page, "Depth", 60);
  await setRange(page, "Stroke", 30);
  assert.equal(await page.getByLabel("Stroke", { exact: true }).evaluate((input) => getComputedStyle(input).getPropertyValue("--range-progress")), "50%");
  await button(page, "Stop").click();
  await until(() => page.getByLabel("Speed", { exact: true }).inputValue(), "0", "zero after Stop");
  assert.equal(await page.getByLabel("Speed", { exact: true }).evaluate((input) => getComputedStyle(input).getPropertyValue("--range-progress")), "0%");
  assert.deepEqual(errors, []);
});

test("semantic statuses actions and notices use their matching colours in both themes", options, async (t) => {
  const fixture = await createBrowserFixture(t, browser);
  const admin = await fixture.openAdmin(userState(deviceState()));
  const guest = await fixture.openGuest("semantic-colours");
  for (const view of [admin, guest]) await view.page.emulateMedia({ reducedMotion: "reduce" });
  for (const theme of ["light", "dark"]) {
    for (const view of [admin, guest])
      await view.page.evaluate((value) => { document.documentElement.dataset.theme = value; }, theme);
    for (const [device, label, token] of [
      [deviceState({ isConnected: false, isConnecting: true }), "Connecting", "--warning"],
      [deviceState(), "Connected", "--success"],
      [deviceState({ isConnected: false, connectionFailed: true }), "Failed", "--danger"],
      [deviceState({ isConnected: false }), "Disconnected", "--neutral"],
    ]) {
      admin.session.state = { ...admin.session.state, device };
      const status = admin.page.getByTestId("device-connection-status");
      await until(() => status.innerText(), label, label);
      await matchesToken(status, "color", token);
    }
    admin.session.state = { ...admin.session.state, guestControlAllowed: true, device: deviceState() };
    const status = admin.page.getByTestId("guest-status");
    await until(() => status.innerText(), "Guest allowed", "guest permission");
    await matchesToken(status, "color", "--success");
    await textContrast(status);
    await setRange(guest.page, "Speed", theme === "light" ? 35 : 36);
    await until(() => button(guest.page, "Send").isEnabled(), true, "changed selection");
    await guest.page.mouse.move(0, 0);
    await matchesToken(button(guest.page, "Send"), "backgroundColor", "--success");
    await matchesToken(button(guest.page, "Stop"), "backgroundColor", "--danger");
    await matchesToken(button(guest.page, "Reset Position"), "backgroundColor", "--warning-fill");
    for (const name of ["Send", "Stop", "Reset Position"]) await textContrast(button(guest.page, name));
    await button(guest.page, "Send").click();
    const sentStatus = guest.page.getByRole("status").filter({ hasText: "Settings sent." });
    await sentStatus.waitFor();
    await matchesToken(sentStatus, "color", "--success");
    await textContrast(sentStatus);
    await button(admin.page, "Reset Position").click();
    const pending = admin.page.getByRole("status").filter({ hasText: "Resetting position ..." });
    await pending.waitFor();
    await matchesToken(pending, "borderLeftColor", "--warning");
    await textContrast(pending);
    await complete(admin.session, "ResetPosition", { accepted: true, code: "position-reset" });
    const completed = admin.page.getByRole("status").filter({ hasText: "Position reset. Stopped." });
    await completed.waitFor();
    await matchesToken(completed, "borderLeftColor", "--success");
    await textContrast(completed);
    await button(admin.page, "Stop").click();
    await complete(admin.session, "Stop", { accepted: false, code: "device-io-failed" });
    const failure = admin.page.getByRole("alert").filter({ hasText: "The action was rejected" });
    await failure.waitFor();
    await matchesToken(failure, "borderLeftColor", "--danger");
    await textContrast(failure);
    for (const name of ["Speed", "Stroke", "Depth"]) await textContrast(guest.page.getByLabel(name, { exact: true }));
  }
  await guest.page.emulateMedia({ forcedColors: "active" });
  assert.equal(await button(guest.page, "Simple stroke").isVisible(), true);
  assert.equal(await preview(guest.page).isVisible(), true);
  assert.deepEqual(admin.errors, []);
  assert.deepEqual(guest.errors, []);
});

test("pattern buttons support keyboard selection and visible focus in both themes", options, async (t) => {
  const fixture = await createBrowserFixture(t, browser);
  const { page, session, errors } = await fixture.openGuest("pattern-keyboard");
  for (const theme of ["light", "dark"]) {
    await page.evaluate((value) => { document.documentElement.dataset.theme = value; }, theme);
    await button(page, "Simple stroke").focus();
    await page.keyboard.press("Tab");
    assert.equal(await button(page, "Teasing / pounding").evaluate((element) => element === document.activeElement), true);
    await page.keyboard.press("Space");
    assert.equal(await button(page, "Teasing / pounding").getAttribute("aria-pressed"), "true");
    await page.keyboard.press("Tab");
    await page.keyboard.press("Enter");
    const selected = button(page, "Robo stroke");
    assert.equal(await selected.getAttribute("aria-pressed"), "true");
    assert.equal(await page.getByRole("group", { name: "Pattern", exact: true }).getByRole("button", { pressed: true }).count(), 1);
    const focus = await selected.evaluate((element) => {
      const style = getComputedStyle(element);
      return { style: style.outlineStyle, width: parseFloat(style.outlineWidth), visible: element.matches(":focus-visible") };
    });
    assert.ok(focus.visible && focus.style !== "none" && focus.width >= 2);
    await textContrast(selected);
    assert.equal(session.requests.length, 0, "Selecting a pattern does not send it in manual mode");
  }
  assert.deepEqual(errors, []);
});

test("native SVG pauses and resumes with reduced motion retaining a still preview", options, async (t) => {
  const fixture = await createBrowserFixture(t, browser);
  const { page, errors } = await fixture.openGuest("reduced-motion");
  await setRange(page, "Speed", 35);
  await until(() => preview(page).getAttribute("aria-label"), (label) => label.includes("35% speed"), "active preview");
  await motion(page, false);
  await button(page, "Send").click();
  await sent(page, "Speed", 35);
  await motion(page, true);
  await page.emulateMedia({ reducedMotion: "reduce" });
  assert.equal(await preview(page).isVisible(), true);
  assert.equal(await preview(page).locator(".preview-carriage").isVisible(), false);
  assert.equal(await preview(page).locator(".preview-still").isVisible(), true);
  await motion(page, false);
  await page.emulateMedia({ reducedMotion: "no-preference" });
  await motion(page, true);
  await button(page, "Stop").click();
  await until(() => page.getByLabel("Speed", { exact: true }).inputValue(), "0", "Stop pauses native SVG");
  await motion(page, false);
  assert.deepEqual(errors, []);
});

async function noOverflow(page) {
  // Allow real layout/style work to settle before observing viewport overflow.
  await page.evaluate(() => new Promise((resolve) => requestAnimationFrame(() => requestAnimationFrame(resolve))));
  assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
}

async function desktopFit(page) {
  await noOverflow(page);
  const size = await page.evaluate(() => ({
    width: innerWidth, height: innerHeight, scrollHeight: document.documentElement.scrollHeight,
  }));
  assert.ok(size.scrollHeight <= size.height, `Vertical overflow: ${JSON.stringify(size)}`);
}

async function matchesToken(locator, property, token) {
  const colours = await locator.evaluate((element, { property, token }) => {
    const style = getComputedStyle(element);
    const resolved = document.createElement("span");
    resolved.style.color = style.getPropertyValue(token);
    document.body.append(resolved);
    const expected = getComputedStyle(resolved).color;
    resolved.remove();
    return { actual: style[property], expected };
  }, { property, token });
  assert.equal(colours.actual, colours.expected, `${property} matches ${token}`);
}

async function textContrast(locator) {
  const colours = await locator.evaluate((element) => {
    const style = getComputedStyle(element);
    let background = element;
    while (background && getComputedStyle(background).backgroundColor === "rgba(0, 0, 0, 0)") background = background.parentElement;
    return { foreground: style.color, background: getComputedStyle(background).backgroundColor };
  });
  const luminance = (colour) => {
    const channels = colour.match(/[\d.]+/g).slice(0, 3).map((value) => {
      const channel = Number(value) / 255;
      return channel <= .04045 ? channel / 12.92 : ((channel + .055) / 1.055) ** 2.4;
    });
    return channels.reduce((sum, channel, index) => sum + channel * [.2126, .7152, .0722][index], 0);
  };
  const values = [luminance(colours.foreground), luminance(colours.background)].sort((a, b) => a - b);
  const ratio = (values[1] + .05) / (values[0] + .05);
  assert.ok(ratio >= 4.5, `Text contrast ${ratio.toFixed(2)}:1: ${JSON.stringify(colours)}`);
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
