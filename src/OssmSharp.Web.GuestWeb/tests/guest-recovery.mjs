import assert from "node:assert/strict";
import { after, before, test } from "node:test";
import {
  assertCapabilityAbsent, button, complete, createBrowserFixture, deviceState,
  guestState, launchBrowser, observePage, preview, sent, setRange, until, userState, waitFor,
} from "./fixtures/browser-fixture.mjs";

let browser;
before(async () => { browser = await launchBrowser(); }, { timeout: 20_000 });
after(async () => { await browser?.close(); }, { timeout: 20_000 });
const options = { timeout: 60_000 };

async function ready(page) {
  await until(() => button(page, "Send").isEnabled(), true, "saved-session attachment");
}

async function assertFreshController(page) {
  assert.equal(await page.getByLabel("Speed", { exact: true }).inputValue(), "0");
  assert.equal(await page.getByLabel("Auto send", { exact: true }).isChecked(), false);
  assert.match(await page.getByLabel("Speed", { exact: true }).getAttribute("aria-valuetext"), /Sent value: none\./);
  await setRange(page, "Speed", 35);
  await until(() => preview(page).getAttribute("aria-label"), (label) => label.includes("35% speed"), "paused selection preview");
  assert.equal(await preview(page).evaluate((rail) => rail.querySelectorAll("animate").length), 0);
  await setRange(page, "Speed", 0);
  await until(() => preview(page).getAttribute("aria-label"), (label) => label.includes("0% speed"), "zero-speed selection");
}

async function assertSecretBoundary(view, session, serviceKey) {
  assertCapabilityAbsent(view, [session.saved.invitationToken, session.saved.credential], serviceKey);
  const exposed = await view.page.evaluate(() => JSON.stringify({
    cookies: document.cookie, local: Object.entries(localStorage), session: Object.entries(sessionStorage), dom: document.body.innerHTML,
  }));
  assert.equal(exposed.includes(session.saved.credential), false);
  assert.equal(exposed.includes(session.saved.invitationToken), false);
}

test("public saved cookie survives refresh and a reopened browser context without another redemption", options, async (t) => {
  const fixture = await createBrowserFixture(t, browser);
  const context = await fixture.context({ ignoreHTTPSErrors: true });
  const redemption = context.waitForEvent("response", {
    predicate: (response) => new URL(response.url()).pathname === "/guest-session/redeem",
  });
  const view = await fixture.openGuest("persistent-public-invitation", { publicHost: true, context });
  const { page, session, host } = view;
  await assertFreshController(page);
  const cookie = (await context.cookies()).find((cookie) => cookie.name === "ossm-guest-session");
  assert.ok(cookie);
  assert.equal(cookie.httpOnly, true);
  assert.equal(cookie.secure, true);
  assert.equal(cookie.sameSite, "Strict");
  assert.equal(cookie.domain, "guest.test");
  assert.equal(cookie.path, "/");
  const headers = await (await redemption).headersArray();
  const savedCookie = headers.find(({ name, value }) => name.toLowerCase() === "set-cookie" && value.startsWith("ossm-guest-session="));
  assert.ok(savedCookie);
  // Browsers adjust Expires for clock skew; the emitted deadline must still match the API's fixed expiry.
  const deadline = /(?:^|;\s*)expires=([^;]+)/i.exec(savedCookie.value);
  assert.ok(deadline);
  assert.equal(Date.parse(deadline[1]), Math.floor(Date.parse(session.saved.expiresAt) / 1000) * 1000);
  assert.ok(cookie.expires > 0, "The browser must persist the cookie");
  await assertSecretBoundary(view, session, fixture.fixture.adminKey);
  await setRange(page, "Speed", 53);
  await button(page, "Send").click();
  await sent(page, "Speed", 53);
  await page.getByLabel("Auto send", { exact: true }).check();
  const previousOwner = session.saved.owner;
  const count = session.requests.length;

  await page.reload();
  await ready(page);
  await assertFreshController(page);
  assert.ok(session.saved.owner !== previousOwner, "Refresh must attach a new controller");
  assert.equal(session.requests.length, count, "Refresh must not replay settings");
  assert.equal((await context.cookies()).find((cookie) => cookie.name === "ossm-guest-session").expires, cookie.expires);
  const storageState = await context.storageState();
  await context.close();
  await waitFor(() => session.saved.owner === null, "closed browser controller detachment");
  const returning = await fixture.context({ storageState, ignoreHTTPSErrors: true });
  const returned = await observePage(returning);
  await returned.page.goto(`${host.origin}/join`);
  await ready(returned.page);
  await assertFreshController(returned.page);
  assert.equal(session.requests.length, count);
  assert.equal(session.state.controlAllowed, true);
  assert.equal(session.calls.filter((target) => target === "Redeem").length, 1);
  assert.equal((await returning.cookies()).find((cookie) => cookie.name === "ossm-guest-session").expires, cookie.expires);
  await assertSecretBoundary(returned, session, fixture.fixture.adminKey);
  assert.equal(returned.errors.length, 0);
});

test("API and browser transport loss recover through fresh controllers without replay", options, async (t) => {
  const fixture = await createBrowserFixture(t, browser);
  const view = await fixture.openGuest("recoverable-public-invitation", { publicHost: true });
  const { page, session, host } = view;
  await setRange(page, "Speed", 35);
  await button(page, "Send").click();
  await sent(page, "Speed", 35);
  await page.getByLabel("Auto send", { exact: true }).check();
  const count = session.requests.length;
  session.holdDisconnectStop = true;
  const oldApi = session.saved.owner;
  oldApi.wire.socket.destroy();
  const notice = page.getByTestId("guest-disconnected");
  await notice.waitFor({ state: "visible" });
  await until(() => notice.innerText(), (text) => text.includes("Waiting for Stop"), "required Stop barrier");
  assert.equal(session.saved.owner, null);
  assert.equal(session.requests.length, count);
  assert.equal(session.state.controlAllowed, true);

  session.holdDisconnectStop = false;
  session.saved.stopPending = false;
  await ready(page);
  await assertFreshController(page);
  assert.ok(session.saved.owner !== oldApi, "API recovery must not reuse a disposed client");
  assert.equal(session.requests.length, count);
  const oldCircuit = session.saved.owner;
  host.proxy.disconnect();
  await waitFor(() => session.saved.owner !== null && session.saved.owner !== oldCircuit, "fresh controller after browser transport loss");
  await ready(page);
  await until(() => page.getByLabel("Speed", { exact: true }).inputValue(), "0", "browser transport recovery");
  await assertFreshController(page);
  assert.ok(session.saved.owner !== oldCircuit, "Browser recovery must attach a fresh circuit client");
  assert.equal(session.requests.length, count);
  assert.equal(session.state.controlAllowed, true);
  assert.equal(session.calls.filter((target) => target === "Redeem").length, 1);
  await setRange(page, "Speed", 42);
  await button(page, "Send").click();
  await sent(page, "Speed", 42);
  assert.equal(session.requests.length, count + 1);
});

test("a second tab cannot interrupt the first and attaches only after the first closes", options, async (t) => {
  const fixture = await createBrowserFixture(t, browser);
  const first = await fixture.openGuest("exclusive-browser-session");
  const { session, context, host } = first;
  await setRange(first.page, "Speed", 35);
  await button(first.page, "Send").click();
  await sent(first.page, "Speed", 35);
  const owner = session.saved.owner;
  const stops = session.disconnectStops;
  const second = await observePage(context);
  await second.page.goto(`${host.origin}/join`);
  await until(() => second.page.getByTestId("guest-disconnected").innerText(),
    (text) => text.includes("open in another tab"), "second-tab rejection");
  const leave = second.page.getByTestId("guest-disconnected").getByRole("button", { name: "Leave session", exact: true });
  assert.equal(await leave.isDisabled(), true);
  await leave.dispatchEvent("click");
  const rejected = await second.page.evaluate(async () => {
    const state = await (await fetch("/guest-session")).json();
    const response = await fetch("/guest-session/leave", { method: "POST", headers: { "X-CSRF-TOKEN": state.requestToken } });
    return { status: response.status, result: await response.json() };
  });
  assert.equal(rejected.status, 409);
  assert.equal(rejected.result.code, "guest-already-joined");
  assert.equal((await context.cookies()).some(cookie => cookie.name === "ossm-guest-session"), true);
  assert.ok(session.saved.owner === owner, "The first tab must retain ownership");
  assert.equal(session.disconnectStops, stops, "Checking another tab must not Stop the first");
  assert.equal(session.requests.length, 1);
  await setRange(first.page, "Speed", 40);
  await button(first.page, "Send").click();
  await sent(first.page, "Speed", 40);
  assert.equal(session.requests.length, 2);

  await first.page.close();
  await ready(second.page);
  await assertFreshController(second.page);
  assert.ok(session.saved.owner !== owner, "The waiting tab must obtain a new attachment");
  assert.equal(session.disconnectStops, stops + 1);
  assert.equal(session.requests.length, 2, "Tab recovery must not replay the first tab's motion");
  assert.equal(session.calls.filter((target) => target === "Redeem").length, 1);
});

test("offline Leave invalidates the saved session and clears its cookie", options, async (t) => {
  const fixture = await createBrowserFixture(t, browser);
  const view = await fixture.openGuest("offline-leave-session");
  const { page, context, session } = view;
  session.holdDisconnectStop = true;
  session.saved.owner.wire.socket.destroy();
  const notice = page.getByTestId("guest-disconnected");
  await notice.waitFor({ state: "visible" });
  await notice.getByRole("button", { name: "Leave session", exact: true }).click();
  await until(() => notice.innerText(), (text) => text.includes("You left the session"), "offline Leave completion");
  assert.equal(session.saved.valid, false);
  assert.equal((await context.cookies()).some((cookie) => cookie.name === "ossm-guest-session"), false);
  assert.equal(session.calls.filter((target) => target === "LeaveSavedSession").length, 1);
  assert.equal(session.requests.length, 0);
});

test("invitation refresh revokes an active guest before failed Stop completes", options, async (t) => {
  const fixture = await createBrowserFixture(t, browser);
  const guest = await fixture.openGuest("refresh-revoked-session");
  const admin = await fixture.openAdmin({
    ...userState(deviceState()), invitationConsumed: true,
    guestSessionExpiresAt: guest.session.saved.expiresAt,
  });
  assert.equal(await button(admin.page, "Copy").isDisabled(), true);
  await button(admin.page, "Refresh invitation").click();
  await waitFor(() => admin.session.pending.has("RefreshInvitation"), "admin invitation refresh");
  assert.equal(guest.session.saved.valid, false, "Revocation must not wait for Stop");
  const ended = guest.page.getByTestId("guest-disconnected");
  await until(() => ended.innerText(), (text) => text.includes("Session ended."), "terminal guest notice");
  await ended.waitFor({ state: "visible" });
  assert.equal(await ended.getByRole("button", { name: "Leave session", exact: true }).isHidden(), true);
  await complete(admin.session, "RefreshInvitation", { accepted: false, code: "device-io-failed" });
  await admin.page.getByRole("alert").filter({ hasText: "Guest session ended. Stop was not confirmed" }).waitFor();
  assert.equal(guest.session.requests.length, 0);
  await guest.page.reload();
  await guest.page.getByTestId("guest-invalid").waitFor({ state: "visible" });
  assert.equal((await guest.context.cookies()).some((cookie) => cookie.name === "ossm-guest-session"), false);

  await button(admin.page, "Stop").click();
  await complete(admin.session, "Stop");
  await until(() => button(admin.page, "Refresh invitation").isEnabled(), true, "required Stop completion");
  const nextInvitation = admin.session.state.invitation.token;
  fixture.provision(nextInvitation, guestState());
  const next = await fixture.openGuest(nextInvitation, { context: guest.context });
  await assertFreshController(next.page);
  assert.equal(next.session.saved.valid, true);
  assert.equal(next.session.requests.length, 0);
  assert.equal(guest.session.saved.valid, false);
});
