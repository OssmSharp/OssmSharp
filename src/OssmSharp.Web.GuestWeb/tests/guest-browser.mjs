import assert from "node:assert/strict";
import { after, before, test } from "node:test";
import {
  assertCapabilityAbsent, button, complete, createBrowserFixture, deviceState,
  guestState, launchBrowser, observePage, preview, sent, setRange, settings, until, userState, waitFor,
} from "./fixtures/browser-fixture.mjs";

let browser;
before(async () => { browser = await launchBrowser(); }, { timeout: 20_000 });
after(async () => { await browser?.close(); }, { timeout: 20_000 });
const options = { timeout: 60_000 };

test("public guest manual and auto send wire controls to RPC and leave scrubs capability", options, async (t) => {
  const fixture = await createBrowserFixture(t, browser);
  const invitation = "public-wiring-invite";
  const view = await fixture.openGuest(invitation, { publicHost: true });
  const { page, session } = view;
  await page.getByRole("region", { name: "Requested settings", exact: true }).waitFor();
  await page.getByRole("region", { name: "Pattern preview", exact: true }).waitFor();
  assert.doesNotMatch(await page.getByRole("main", { name: "Guest controls", exact: true }).innerText(), /Last known|Control enabled\./);
  for (const [value, expected] of [[12.4, 12], [12.5, 13], [12.6, 13]]) {
    await setRange(page, "Speed", value);
    assert.equal(await page.getByLabel("Speed", { exact: true }).inputValue(), String(expected), "Native step=1 coercion");
    assert.equal(session.requests.length, 0);
  }
  await button(page, "Stop / go").click();
  await setRange(page, "Sensation", 75);
  await button(page, "Send").click();
  await sent(page, "Speed", 13);
  assert.equal(session.requests.length, 1);
  assert.deepEqual(pickSettings(session.requests[0]), { pattern: 5, speed: 13, stroke: 10, depth: 20, sensation: 75 });
  await page.getByLabel("Auto send", { exact: true }).check();
  await button(page, "Deeper").click();
  await sent(page, "Speed", 13);
  await until(() => page.getByTestId("preview-pattern").innerText(), "Deeper", "Auto preview");
  await waitFor(() => session.requests.length === 2, "Auto Submit");
  assert.deepEqual(pickSettings(session.requests[1]), { pattern: 4, speed: 13, stroke: 10, depth: 20, sensation: 75 });
  await page.getByLabel("Auto send", { exact: true }).uncheck();
  await until(() => page.getByRole("status").filter({ hasText: "Settings sent." }).count(), 1, "send status");
  session.state.device = { ...session.state.device, speed: 33.333333333333336, stroke: 30 };
  await currentSpeed(page, "33.333333333333336");
  await setRange(page, "Depth", 80);
  await until(() => preview(page).getAttribute("aria-label"), (label) => label.includes("80% depth"), "real UpdateDisplay preview");
  assert.equal(session.requests.length, 2, "Preview edits must not Submit");
  await currentSpeed(page, "33.333333333333336");
  session.state.device = { ...session.state.device, speed: 400, stroke: 10 };
  await currentSpeed(page, "400");
  session.state.device = { ...session.state.device, speed: null, stroke: 0 };
  await currentSpeed(page, "unavailable");
  await button(page, "Leave session").click();
  await page.getByRole("alert").filter({ hasText: "You left the session." }).waitFor();
  assert.equal(session.calls.filter((name) => name === "Leave").length, 1);
  assert.equal(session.calls.filter((name) => name === "LeaveSavedSession").length, 0);
  assert.equal((await view.context.cookies()).some((cookie) => cookie.name === "ossm-guest-session"), false);
  assert.equal(new URL(page.url()).hash, "");
  assertCapabilityAbsent(view, [invitation, session.saved.credential], fixture.fixture.adminKey);
  assert.deepEqual(view.errors, []);
});

test("admin invitation keyboard connect and safety actions reach their RPCs", options, async (t) => {
  const fixture = await createBrowserFixture(t, browser);
  const { page, session, errors } = await fixture.openAdmin();
  assert.equal(await page.getByLabel("Guest invitation", { exact: true }).inputValue(), "https://guest.test/join#invite=isolated-admin-invite");
  await page.getByRole("region", { name: "Controls", exact: true }).waitFor();
  await page.getByRole("region", { name: "Device connection", exact: true }).waitFor();
  await page.getByRole("heading", { name: "Last settings", exact: true }).waitFor();
  assert.equal(await button(page, "Reset").count(), 0);
  assert.doesNotMatch(await page.getByRole("main", { name: "User controls", exact: true }).innerText(),
    /Permission expires|Not active|Last known device status|Allow guest/);
  assert.equal(await preview(page).getAttribute("aria-label"), "Unknown preview at Unknown speed, Unknown stroke, Unknown depth, and Unknown sensation");
  await button(page, "Connect device").focus();
  await page.keyboard.press("Enter");
  await waitFor(() => session.pending.has("Connect"), "keyboard Connect");
  await until(() => button(page, "Connect device").isDisabled(), true, "Connect busy");
  assert.equal(await button(page, "Stop").isEnabled(), true);
  assert.equal(await button(page, "End guest").isEnabled(), true);
  await button(page, "Stop").click();
  await complete(session, "Stop");
  await until(() => button(page, "End guest").isEnabled(), true, "safety completion");
  assert.equal(await button(page, "Connect device").isDisabled(), true);
  await button(page, "End guest").click();
  await complete(session, "EndGuest");
  assert.deepEqual(commands(session), ["Connect", "Stop", "EndGuest"]);
  await complete(session, "Connect", { accepted: false, code: "connection-failed" });
  await until(() => button(page, "Connect device").isEnabled(), true, "late Connect settles");
  await page.getByRole("status").filter({ hasText: "Guest session ended." }).waitFor();
  assert.equal(await page.getByRole("alert").filter({ hasText: "The action was rejected (connection-failed)." }).count(), 0);
  assert.equal(await page.getByRole("status").filter({ hasText: "Device connected." }).count(), 0);
  assert.deepEqual(errors, []);
});

test("admin reset position uses its distinct RPC while Stop remains available", options, async (t) => {
  const fixture = await createBrowserFixture(t, browser);
  const { page, session, errors } = await fixture.openAdmin(userState(deviceState({ transmittedSettings: settings })));
  const label = await preview(page).getAttribute("aria-label");
  await button(page, "Reset Position").click();
  await waitFor(() => session.pending.has("ResetPosition"), "ResetPosition");
  await page.getByRole("status").filter({ hasText: "Resetting position ..." }).waitFor();
  assert.equal(await button(page, "Reset Position").isDisabled(), true);
  assert.equal(await button(page, "Stop").isEnabled(), true);
  await button(page, "Stop").click();
  await complete(session, "Stop");
  await complete(session, "ResetPosition", { accepted: false, code: "cancelled" });
  await until(() => button(page, "Reset Position").isEnabled(), true, "reset settles");
  assert.deepEqual(commands(session), ["ResetPosition", "Stop"]);
  assert.equal(await preview(page).getAttribute("aria-label"), label);
  assert.equal(session.state.guestControlAllowed, false);
  assert.deepEqual(errors, []);
});

test("guest reset and acknowledged Stop permit only deliberate recovery", options, async (t) => {
  const fixture = await createBrowserFixture(t, browser);
  const { page, session, errors } = await fixture.openGuest("guest-safety");
  await setRange(page, "Speed", 35);
  await button(page, "Insist").click();
  await page.getByLabel("Auto send", { exact: true }).check();
  await sent(page, "Speed", 35);
  await button(page, "Reset Position").click();
  await waitFor(() => session.pending.has("GuestResetPosition"), "GuestResetPosition");
  await page.getByRole("status").filter({ hasText: "Resetting position ..." }).waitFor();
  assert.equal(await button(page, "Send").isDisabled(), true);
  assert.equal(await page.getByLabel("Auto send", { exact: true }).isChecked(), false);
  assert.equal(await page.getByLabel("Speed", { exact: true }).inputValue(), "35");
  assert.equal(await button(page, "Insist").getAttribute("aria-pressed"), "true");
  assert.equal(await button(page, "Stop").isEnabled(), true);
  session.holdStop = true;
  await button(page, "Stop").click();
  await complete(session, "GuestStop", { accepted: false, code: "outcome-unknown" });
  await page.getByRole("alert").filter({ hasText: "Stop was not acknowledged." }).waitFor();
  await complete(session, "GuestResetPosition", { accepted: false, code: "cancelled" });
  assert.equal(await button(page, "Send").isDisabled(), true);
  await button(page, "Stop").click();
  session.state = guestState({ canResumeAfterStop: true, device: deviceState({ isArmed: false }) });
  await complete(session, "GuestStop");
  await until(() => button(page, "Send").isEnabled(), true, "acknowledged Stop recovery");
  assert.equal(session.requests.length, 1);
  await setRange(page, "Speed", 35);
  await button(page, "Send").click();
  await sent(page, "Speed", 35);
  assert.equal(session.requests.length, 2);
  session.state.controlAllowed = false;
  await page.getByRole("status").filter({ hasText: "Connected. Waiting for the other person to allow control." }).waitFor();
  assert.equal(await page.getByLabel("Speed", { exact: true }).inputValue(), "0");
  assert.equal(await button(page, "Send").isDisabled(), true);
  session.state = guestState({ device: deviceState({ isArmed: false }) });
  await until(() => page.getByRole("status").filter({ hasText: "Waiting for the other person to start." }).count(), 0, "permission restored");
  assert.equal(await button(page, "Send").isDisabled(), true, "Permission cannot bypass a disarmed device");
  session.state.device.isArmed = true;
  await until(() => button(page, "Send").isEnabled(), true, "positive readiness");
  assert.equal(session.requests.length, 2);
  assert.equal(await page.getByLabel("Auto send", { exact: true }).isChecked(), false);
  assert.equal(await page.getByTestId("guest-disconnected").isVisible(), false);
  assert.equal(session.calls.includes("Renew"), false);
  assert.deepEqual(commands(session), ["Submit", "GuestResetPosition", "GuestStop", "GuestStop", "Submit"]);
  assert.deepEqual(errors, []);
});

test("rejected invitation renders invalid without a transport-loss view", options, async (t) => {
  const fixture = await createBrowserFixture(t, browser);
  const host = await fixture.publicHost();
  const context = await fixture.context({ ignoreHTTPSErrors: true });
  const { page } = await observePage(context);
  await page.goto(`${host.origin}/join#invite=rejected-invite`);
  await page.getByTestId("guest-invalid").waitFor({ state: "visible" });
  assert.equal(await page.getByTestId("guest-disconnected").isVisible(), false);
  assert.equal(await page.getByRole("region", { name: "Requested settings", exact: true }).count(), 0);
  assert.equal(new URL(page.url()).hash, "");
});

test("initial API rejection renders a terminal error without a reload loop or fabricated guest controls", options, async (t) => {
  const fixture = await createBrowserFixture(t, browser);
  const session = fixture.provision("initial-api-failure");
  session.failReads = true;
  const host = await fixture.guestHost();
  const context = await fixture.context();
  const { page, errors } = await observePage(context);
  await page.goto(`${host.origin}/join#invite=initial-api-failure`);
  await page.getByTestId("guest-disconnected").waitFor({ state: "visible" });
  assert.equal(await page.getByRole("alert").filter({ hasText: "The guest page could not start." }).count(), 1);
  assert.equal(await page.getByRole("region", { name: "Requested settings", exact: true }).count(), 0);
  assert.equal(await page.getByTestId("guest-invalid").count(), 0);
  assert.equal(new URL(page.url()).hash, "");
  assert.equal(session.saved.owner, null);
  assert.equal(fixture.fixture.calls.filter(({ target }) => target === "Join").length, 1);
  assert.deepEqual(errors, []);
});

test("public circuit loss disposes its API connection and pauses controls until Stop permits fresh recovery", options, async (t) => {
  const fixture = await createBrowserFixture(t, browser);
  const { page, session, host } = await fixture.openGuest("public-circuit-loss", { publicHost: true });
  const owner = session.saved.owner;
  session.holdDisconnectStop = true;
  assert.equal(owner.closed, false);
  host.proxy.disconnect();
  await waitFor(() => owner.closed, "GuestCircuitHandler API disposal", 10_000);
  await page.getByTestId("guest-disconnected").waitFor({ state: "visible" });
  assert.equal(await button(page, "Send").isDisabled(), true);
  assert.equal(await page.evaluate(() => sessionStorage.getItem("ossm-guest-connection-lost")), null);
  assert.equal(session.state.controlAllowed, true);
  assert.equal(session.disconnectStops, 1);
  session.holdDisconnectStop = false;
  session.saved.stopPending = false;
  await until(() => button(page, "Send").isEnabled(), true, "fresh circuit attachment");
  assert.ok(session.saved.owner !== owner);
  assert.equal(await page.getByLabel("Speed", { exact: true }).inputValue(), "0");
  assert.equal(await page.getByLabel("Auto send", { exact: true }).isChecked(), false);
  assert.equal(session.requests.length, 0);
});

test("fresh and same-document invitation links resume saved authority without capability navigation frames", options, async (t) => {
  const fixture = await createBrowserFixture(t, browser);
  const tokens = ["navigation-first", "navigation-fresh", "navigation-link"];
  const view = await fixture.openGuest(tokens[0]);
  fixture.provision(tokens[1]);
  const linked = fixture.provision(tokens[2]);
  await view.page.goto(`${view.host.origin}/join#invite=${tokens[1]}`);
  await waitFor(() => view.session.calls.filter((target) => target === "Join").length === 2, "fresh document saved-session attachment");
  await until(() => new URL(view.page.url()).hash, "", "fresh invitation scrubbing");
  await until(() => button(view.page, "Send").isEnabled(), true, "fresh document invitation");
  assert.equal(new URL(view.page.url()).hash, "");
  const main = view.page.getByRole("main", { name: "Guest controls", exact: true });
  await main.evaluate((element, token) => {
    const link = document.createElement("a");
    link.href = `/join#invite=${token}`;
    link.textContent = "Reopen test invitation";
    element.append(link);
  }, tokens[2]);
  await view.page.getByRole("link", { name: "Reopen test invitation", exact: true }).click();
  await waitFor(() => view.session.calls.filter((target) => target === "Join").length === 3, "same-document saved-session attachment");
  await until(() => button(view.page, "Send").isEnabled(), true, "same-document readiness");
  assert.equal(new URL(view.page.url()).hash, "");
  const joins = fixture.fixture.calls.filter(({ target }) => target === "Join").map(({ session }) => session.invitationToken);
  assert.deepEqual(joins, [tokens[0], tokens[0], tokens[0]]);
  assert.equal(linked.calls.length, 0);
  assert.equal(view.session.calls.filter((target) => target === "Redeem").length, 1);
  assertCapabilityAbsent(view, [...tokens, view.session.saved.credential], fixture.fixture.adminKey);
  assert.deepEqual(view.errors, []);
});

test("polling an ended guest retains ended rather than disconnected semantics", options, async (t) => {
  const fixture = await createBrowserFixture(t, browser);
  const { page, session, errors } = await fixture.openGuest("poll-ended", { state: guestState({ device: null }), ready: false });
  assert.equal(await button(page, "Send").isDisabled(), true);
  assert.equal(await page.getByTestId("guest-disconnected").isVisible(), false);
  session.state = guestState();
  await until(() => button(page, "Send").isEnabled(), true, "initial device recovery");
  assert.equal(session.requests.length, 0);
  session.state = guestState({ joined: false, controlAllowed: false, device: null });
  session.saved.valid = false;
  const notice = page.getByTestId("guest-disconnected");
  await until(() => notice.innerText(), (text) => text.includes("Session ended."), "ended guest notice");
  await notice.waitFor({ state: "visible" });
  assert.doesNotMatch(await notice.innerText(), /Connection lost|Reconnecting/);
  assert.equal(await notice.getByRole("button", { name: "Leave session", exact: true }).isHidden(), true);
  assert.equal(await button(page, "Send").count(), 0);
  assert.equal(await page.evaluate(() => sessionStorage.getItem("ossm-guest-connection-lost")), null);
  assert.equal(session.calls.includes("Renew"), false);
  assert.deepEqual(errors, []);
});

test("admin metadata timer and API loss retain last requested state", options, async (t) => {
  const fixture = await createBrowserFixture(t, browser);
  const name = "Isolated device";
  const identifier = "isolated-device-id";
  const state = userState(deviceState({ name, identifier, isArmed: false, connectedAt: new Date(Date.now() - 3_661_000).toISOString(), transmittedSettings: settings }));
  const view = await fixture.openAdmin(state);
  const { page, session, errors } = view;
  assert.equal(await page.getByTestId("device-name").innerText(), name);
  assert.equal(await page.getByTestId("device-identifier").innerText(), identifier);
  assert.equal(await page.getByTestId("device-connection-status").innerText(), "Connected");
  assert.doesNotMatch(await page.getByRole("main", { name: "User controls", exact: true }).innerText(),
    /Connecting\.\.\.|Device connected\.|Device connection requested\./);
  const duration = page.getByTestId("device-duration");
  await until(() => duration.innerText(), (value) => /^01:01:\d{2}$/.test(value), "real JS duration");
  session.holdReads = true;
  await waitFor(() => session.pending.has("GetUserState"), "poll held without render");
  const before = seconds(await duration.innerText());
  const frameCount = view.renderFrames.length;
  await until(() => duration.innerText(), (value) => seconds(value) >= before + 2, "local timer advancement");
  assert.equal(view.renderFrames.length, frameCount, "Local timer must not require a render batch");
  const label = await preview(page).getAttribute("aria-label");
  await session.api.wire.dispose();
  await page.getByRole("alert").filter({ hasText: "Unable to connect to the user service." }).waitFor();
  assert.equal(await page.getByTestId("device-name").innerText(), name);
  assert.equal(await page.getByTestId("device-identifier").innerText(), identifier);
  assert.equal(await preview(page).getAttribute("aria-label"), label);
  assert.deepEqual(errors, []);
});

test("component import failure leaves one bootstrap fallback notice", options, async (t) => {
  const fixture = await createBrowserFixture(t, browser);
  fixture.provision("component-failure");
  const host = await fixture.guestHost();
  const context = await fixture.context();
  let bootstrapLoaded = false;
  let componentImportAborted = false;
  await context.addInitScript(() => {
    document.addEventListener("readystatechange", () => {
      const bootstrap = document.getElementById("guest-start");
      const controller = new URL(bootstrap.dataset.controller, document.baseURI);
      controller.search = "?bootstrap-test";
      bootstrap.dataset.controller = controller.href;
    }, { once: true });
  });
  await context.route(/\/guest-session(?:\.[^/]+)?\.js(?:\?.*)?$/, async (route) => {
    if (new URL(route.request().url()).search === "?bootstrap-test") {
      bootstrapLoaded = true;
      await route.continue();
    } else {
      componentImportAborted = true;
      await route.abort("failed");
    }
  });
  const { page } = await observePage(context);
  await page.goto(`${host.origin}/join#invite=component-failure`);
  await page.getByRole("alert").filter({ hasText: "The guest page could not start." }).waitFor();
  assert.equal(bootstrapLoaded, true);
  assert.equal(componentImportAborted, true);
  assert.equal(await page.getByRole("alert").filter({ hasText: "The guest page could not start." }).count(), 1);
  assert.equal(await page.getByRole("region", { name: "Requested settings", exact: true }).count(), 0);
  assert.equal(await page.getByRole("region", { name: "Pattern preview", exact: true }).count(), 0);
  assert.equal(await page.getByTestId("guest-invalid").isVisible(), true);
  assert.equal(await page.getByTestId("guest-disconnected").isVisible(), false);
  assert.equal(fixture.fixture.calls.filter(({ target }) => target === "Join").length, 0);
  assert.equal(fixture.fixture.calls.filter(({ target }) => target === "Redeem").length, 1);
});

test("guest API loss keeps stale controls locked through markup replacement and reload until safe recovery", options, async (t) => {
  const fixture = await createBrowserFixture(t, browser);
  const { page, session, host, errors } = await fixture.openGuest("persistent-api-loss");
  await setRange(page, "Speed", 50);
  const originalSend = await button(page, "Send").elementHandle();
  session.holdDisconnectStop = true;
  await session.api.wire.dispose();
  const notice = page.getByTestId("guest-disconnected");
  await notice.waitFor({ state: "visible" });
  for (const name of ["Send", "Stop", "Reset Position"]) assert.equal(await button(page, name).isDisabled(), true);
  assert.equal(await page.getByRole("complementary", { name: "Pattern", exact: true })
    .getByRole("button", { name: "Leave session", exact: true }).isDisabled(), true);
  assert.equal(await notice.getByRole("button", { name: "Leave session", exact: true }).isEnabled(), true);
  assert.equal(await page.getByLabel("Auto send", { exact: true }).isDisabled(), true);
  assert.equal(await page.getByLabel("Speed", { exact: true }).inputValue(), "0");
  assert.match(await notice.innerText(), /Connection lost|Waiting for Stop/);
  assert.equal(await page.evaluate(() => sessionStorage.getItem("ossm-guest-connection-lost")), null);
  // A real fresh layout fragment replaces only the recovery notice, not the circuit.
  const freshPage = await page.context().newPage();
  await freshPage.goto(`${host.origin}/join`);
  const freshNotice = await freshPage.getByTestId("guest-disconnected").evaluate((element) => element.cloneNode(true).outerHTML);
  await freshPage.close();
  await notice.evaluate((element, html) => {
    const template = document.createElement("template");
    template.innerHTML = html;
    element.replaceWith(template.content.firstElementChild);
  }, freshNotice);
  await notice.waitFor({ state: "visible" });
  const count = session.requests.length;
  await page.evaluate(async () => {
    const controller = document.getElementById("guest-start").dataset.controller;
    (await import(new URL(controller, document.baseURI).href)).updateState({ joined: true, ready: true });
  });
  await originalSend.evaluate((element) => element.click());
  assert.equal(await button(page, "Send").isDisabled(), true);
  assert.equal(session.requests.length, count);
  await originalSend.dispose();
  await page.reload();
  await notice.waitFor({ state: "visible" });
  assert.equal(await button(page, "Send").count(), 0);
  assert.equal(await page.getByTestId("guest-invalid").isVisible(), false);
  assert.equal(await page.evaluate(() => sessionStorage.getItem("ossm-guest-connection-lost")), null);
  session.holdDisconnectStop = false;
  session.saved.stopPending = false;
  await until(() => button(page, "Send").isEnabled(), true, "safe saved-session recovery");
  assert.equal(await page.getByLabel("Speed", { exact: true }).inputValue(), "0");
  assert.equal(await page.getByLabel("Auto send", { exact: true }).isChecked(), false);
  assert.match(await page.getByLabel("Speed", { exact: true }).getAttribute("aria-valuetext"), /Sent value: none\./);
  assert.equal(session.requests.length, count);
  assert.deepEqual(errors, []);
});

function pickSettings(request) {
  return Object.fromEntries(["pattern", "speed", "stroke", "depth", "sensation"].map((name) => [name, request[name]]));
}

function commands(session) {
  return session.calls.filter((name) => !["Redeem", "GetSavedSession", "Join", "GetGuestState", "GetUserState", "SetPreviewDimensions"].includes(name));
}

async function currentSpeed(page, value) {
  await until(() => page.getByLabel("Speed", { exact: true }).getAttribute("aria-valuetext"),
    (text) => text?.includes(`Current value: ${value}.`), "exact current speed");
}

function seconds(value) {
  return /^\d{2}:\d{2}:\d{2}$/.test(value) ? value.split(":").reduce((total, part) => total * 60 + Number(part), 0) : NaN;
}
