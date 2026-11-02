import assert from "node:assert/strict";
import { after, before, test } from "node:test";
import {
  assertCapabilityAbsent, button, createBrowserFixture,
  launchBrowser, observePage, preview, sent, setRange, until, waitFor,
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

test("public browser circuit loss disposes the API client and waits for Stop before fresh recovery", options, async (t) => {
  const fixture = await createBrowserFixture(t, browser);
  const view = await fixture.openGuest("recoverable-public-invitation", { publicHost: true });
  const { page, session, host } = view;
  await setRange(page, "Speed", 35);
  await button(page, "Send").click();
  await sent(page, "Speed", 35);
  await page.getByLabel("Auto send", { exact: true }).check();
  const count = session.requests.length;
  session.holdDisconnectStop = true;
  const oldCircuit = session.saved.owner;
  assert.equal(oldCircuit.closed, false);
  host.proxy.disconnect();
  await waitFor(() => oldCircuit.closed, "GuestCircuitHandler API disposal", 10_000);
  const notice = page.getByTestId("guest-disconnected");
  await notice.waitFor({ state: "visible" });
  await until(() => notice.innerText(), (text) => text.includes("Waiting for Stop"), "required Stop barrier");
  assert.equal(session.saved.owner, null);
  assert.equal(session.requests.length, count);
  assert.equal(session.state.controlAllowed, true);
  assert.equal(session.disconnectStops, 1);
  assert.equal(await button(page, "Send").isDisabled(), true);
  assert.equal(await page.evaluate(() => sessionStorage.getItem("ossm-guest-connection-lost")), null);

  session.holdDisconnectStop = false;
  session.saved.stopPending = false;
  await ready(page);
  await assertFreshController(page);
  assert.ok(session.saved.owner !== oldCircuit, "Browser recovery must attach a fresh circuit client");
  assert.equal(session.requests.length, count);
  assert.equal(session.state.controlAllowed, true);
  assert.equal(session.calls.filter((target) => target === "Redeem").length, 1);
  await setRange(page, "Speed", 42);
  await button(page, "Send").click();
  await sent(page, "Speed", 42);
  assert.equal(session.requests.length, count + 1);
  assert.deepEqual(view.errors, []);
});
