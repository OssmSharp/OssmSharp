import assert from "node:assert/strict";
import { after, before, test } from "node:test";
import {
  assertCapabilityAbsent, button, createBrowserFixture, deviceState,
  launchBrowser, observePage, sent, setRange, settings, until, userState, waitFor,
} from "./fixtures/browser-fixture.mjs";

let browser;
before(async () => { browser = await launchBrowser(); }, { timeout: 20_000 });
after(async () => { await browser?.close(); }, { timeout: 20_000 });
const options = { timeout: 60_000 };

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

test("admin metadata timer advances locally without a Blazor render", options, async (t) => {
  const fixture = await createBrowserFixture(t, browser);
  const name = "Isolated device";
  const identifier = "isolated-device-id";
  const now = new Date("2030-01-01T01:01:01Z");
  const state = userState(deviceState({ name, identifier, isArmed: false, connectedAt: "2030-01-01T00:00:00Z", transmittedSettings: settings }));
  // Install the clock before the real module registers its interval.
  const host = await fixture.adminHost();
  const context = await fixture.context();
  fixture.admin.state = state;
  const view = { ...await observePage(context), session: fixture.admin };
  await view.page.clock.install();
  await view.page.clock.setFixedTime(now);
  await view.page.goto(host.origin);
  const { page, session, errors } = view;
  await until(() => page.getByTestId("device-name").innerText(), name, "loaded metadata");
  assert.equal(await page.getByTestId("device-identifier").innerText(), identifier);
  assert.equal(await page.getByTestId("device-connection-status").innerText(), "Connected");
  assert.doesNotMatch(await page.getByRole("main", { name: "User controls", exact: true }).innerText(),
    /Connecting\.\.\.|Device connected\.|Device connection requested\./);
  const duration = page.getByTestId("device-duration");
  await until(() => duration.innerText(), "01:01:01", "real JS duration");
  session.holdReads = true;
  await waitFor(() => session.pending.has("GetUserState"), "poll held without render");
  const frameCount = view.renderFrames.length;
  // Drive the real registered interval, not a replacement implementation.
  await page.clock.setFixedTime(new Date(now.valueOf() + 2000));
  await page.clock.runFor(2000);
  assert.equal(await duration.innerText(), "01:01:03");
  assert.equal(view.renderFrames.length, frameCount, "Local timer must not require a render batch");
  assert.deepEqual(errors, []);
});

test("guest API transport loss locks controls until Stop permits a fresh controller without replay", options, async (t) => {
  const fixture = await createBrowserFixture(t, browser);
  const { page, session, errors } = await fixture.openGuest("persistent-api-loss");
  await setRange(page, "Speed", 50);
  await button(page, "Send").click();
  await sent(page, "Speed", 50);
  await page.getByLabel("Auto send", { exact: true }).check();
  const owner = session.saved.owner;
  const count = session.requests.length;
  session.holdDisconnectStop = true;
  await session.api.wire.dispose();
  const notice = page.getByTestId("guest-disconnected");
  await notice.waitFor({ state: "visible" });
  for (const name of ["Send", "Stop", "Reset Position"]) assert.equal(await button(page, name).isDisabled(), true);
  assert.equal(await button(page, "Leave session").count(), 0);
  assert.equal(await page.getByLabel("Auto send", { exact: true }).isDisabled(), true);
  assert.equal(await page.getByLabel("Speed", { exact: true }).inputValue(), "0");
  await until(() => notice.innerText(), (text) => text.includes("Waiting for Stop"), "Stop barrier");
  assert.equal(session.saved.owner, null);
  assert.equal(await page.evaluate(() => sessionStorage.getItem("ossm-guest-connection-lost")), null);
  session.holdDisconnectStop = false;
  session.saved.stopPending = false;
  await until(() => button(page, "Send").isEnabled(), true, "safe saved-session recovery");
  assert.equal(await page.getByLabel("Speed", { exact: true }).inputValue(), "0");
  assert.equal(await page.getByLabel("Auto send", { exact: true }).isChecked(), false);
  assert.match(await page.getByLabel("Speed", { exact: true }).getAttribute("aria-valuetext"), /Sent value: none\./);
  assert.equal(session.requests.length, count);
  assert.notEqual(session.saved.owner, owner);
  assert.equal(session.calls.filter((target) => target === "Redeem").length, 1);
  assert.deepEqual(errors, []);
});
