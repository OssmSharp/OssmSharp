import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { setImmediate } from "node:timers/promises";
import { test } from "node:test";
import { createContext, SourceTextModule } from "node:vm";

const source = await readFile(new URL("../wwwroot/guest-session.js", import.meta.url), "utf8");
const disconnectedKey = "ossm-guest-connection-lost";

class Element extends EventTarget {
  attributes = new Map();
  dataset = {};
  value = "0";
  min = "0";
  max = "100";
  checked = false;
  disabled = false;
  paused = false;
  pauseAnimations() {
    this.paused = true;
  }
  setAttribute(name, value) {
    this.attributes.set(name, String(value));
  }
  removeAttribute(name) {
    this.attributes.delete(name);
  }
  toggleAttribute(name, force) {
    force ? this.setAttribute(name, "") : this.removeAttribute(name);
  }
}

async function createGuest(invoke = async () => ({ ok: true }), url = "https://guest.example/join#invite=test-invitation") {
  const elements = new Map();
  const root = new Element();
  root.querySelector = (selector) => {
    if (!elements.has(selector)) elements.set(selector, new Element());
    return elements.get(selector);
  };
  for (const [name, value] of Object.entries({ speed: 30, stroke: 10, depth: 20 }))
    root.querySelector(`[data-input="${name}"]`).value = String(value);
  const storage = new Map();
  const location = new URL(url);
  const reloads = [];
  location.reload = () => reloads.push(location.href);
  const document = Object.assign(new EventTarget(), {
    visibilityState: "visible",
    documentElement: new Element(),
    querySelector: root.querySelector,
  });
  const window = new EventTarget();
  const calls = [];
  const context = createContext({
    AbortController,
    URLSearchParams,
    document,
    window,
    location,
    history: {
      replaceState: (_state, _unused, path) => {
        location.href = new URL(path, location).href;
      },
    },
    sessionStorage: {
      getItem: (key) => storage.get(key) ?? null,
      setItem: (key, value) => storage.set(key, value),
      removeItem: (key) => storage.delete(key),
    },
  });
  const module = new SourceTextModule(source, { context });
  await module.link(() => assert.fail("Unexpected module dependency"));
  await module.evaluate();
  const guest = module.namespace;
  guest.attach(root, {
    invokeMethodAsync: async (method, payload) => {
      calls.push({ method, payload });
      return invoke(method, payload);
    },
  });
  guest.updateState({ joined: true, ready: true });
  await setImmediate();
  return {
    guest,
    root,
    storage,
    location,
    document,
    window,
    reloads,
    element: root.querySelector,
    click: (selector) => root.querySelector(selector).dispatchEvent(new Event("click")),
    calls: (method) => calls.filter((call) => call.method === method),
  };
}

test("stroke-relative speed sends velocity percentages and retains the selected cadence", async () => {
  const browser = await createGuest();
  for (const [speed, stroke, depth, expectedSpeed] of [
    [50, 10, 10, 5],
    [50, 80, 100, 40],
    [50, 10, 80, 5],
    [100, 100, 100, 100],
    [0, 10, 20, 0],
    [30, 1, 1, 0],
    [100, 1, 1, 1],
  ]) {
    for (const [name, value] of Object.entries({ depth, stroke, speed })) {
      const input = browser.element(`[data-input="${name}"]`);
      input.value = String(value);
      input.dispatchEvent(new Event("input"));
    }
    const count = browser.calls("SubmitSettings").length;
    browser.click("[data-send-button]");
    await setImmediate();
    const command = browser.calls("SubmitSettings").at(-1).payload;
    assert.deepEqual([command.speed, command.stroke, command.depth], [expectedSpeed, stroke, depth]);
    const display = browser.calls("UpdateDisplay").at(-1).payload;
    assert.equal(display.selected.speed, speed);
    assert.equal(display.sent.speed, speed);
    assert.equal(browser.element('[data-sent="speed"]').textContent, speed);
    assert.equal(browser.element('[data-sent-marker="speed"]').attributes.get("y1"), String(100 - speed));
    browser.click("[data-send-button]");
    await setImmediate();
    assert.equal(browser.calls("SubmitSettings").length, count + 1);
  }
  browser.guest.dispose();
});

test("unknown outcome pauses queued and new sends until an acknowledged recovery Stop", async () => {
  let completeSubmission;
  let completeStop;
  const browser = await createGuest(async (method) => {
    if (method === "SubmitSettings")
      return new Promise((resolve) => {
        completeSubmission = resolve;
      });
    if (method === "StopRun")
      return new Promise((resolve) => {
        completeStop = resolve;
      });
  });
  browser.click("[data-send-button]");
  await setImmediate();
  browser.element('[data-input="speed"]').value = "40";
  browser.click("[data-send-button]");
  completeSubmission({ ok: false, code: "outcome-unknown" });
  await setImmediate();
  assert.equal(browser.calls("SubmitSettings").length, 1, "queued work must not follow an unknown outcome");
  assert.equal(browser.element("[data-send-button]").disabled, true);
  assert.match(browser.calls("UpdateDisplay").at(-1).payload.error, /may have|unknown/i);
  browser.guest.updateState({ joined: true, ready: true });
  browser.click("[data-send-button]");
  await setImmediate();
  assert.equal(browser.calls("SubmitSettings").length, 1, "polling must not clear uncertainty");
  browser.click("[data-stop-button]");
  browser.guest.updateState({ joined: true, ready: true });
  browser.click("[data-send-button]");
  await setImmediate();
  assert.equal(browser.calls("SubmitSettings").length, 1, "a pending Stop acknowledgement must block sends even when polling says ready");
  assert.equal(browser.element("[data-send-button]").disabled, true);
  completeStop({ ok: false, code: "outcome-unknown" });
  await setImmediate();
  assert.equal(browser.element("[data-send-button]").disabled, true, "unacknowledged Stop cannot recover");
  browser.click("[data-stop-button]");
  completeStop({ ok: true, code: "accepted" });
  await setImmediate();
  browser.click("[data-send-button]");
  await setImmediate();
  assert.equal(browser.calls("SubmitSettings").length, 2);
  completeSubmission({ ok: true });
  await setImmediate();
  browser.guest.dispose();
});

test("identical explicit Send is allowed after Stop and re-enable", async () => {
  const browser = await createGuest();
  browser.click("[data-send-button]");
  await setImmediate();
  browser.click("[data-stop-button]");
  await setImmediate();
  browser.guest.updateState({ joined: true, ready: false });
  browser.guest.updateState({ joined: true, ready: true });
  browser.click("[data-send-button]");
  await setImmediate();
  assert.equal(browser.calls("SubmitSettings").length, 2);
  browser.guest.dispose();
});

test("control revocation cancels queued auto sends and regrant requires deliberate submission", async () => {
  let completeSubmission;
  const browser = await createGuest(async (method) => {
    if (method === "SubmitSettings")
      return new Promise((resolve) => {
        completeSubmission = resolve;
      });
    return { ok: true };
  });
  const autoSend = browser.element("[data-auto-send]");
  autoSend.checked = true;
  autoSend.dispatchEvent(new Event("change"));
  await setImmediate();
  browser.element('[data-input="speed"]').value = "40";
  browser.element('[data-input="speed"]').dispatchEvent(new Event("input"));
  browser.guest.updateState({ joined: true, ready: false });
  assert.equal(autoSend.checked, false);
  assert.equal(browser.element("[data-send-button]").disabled, true);
  completeSubmission({ ok: true });
  await setImmediate();
  assert.equal(browser.calls("SubmitSettings").length, 1, "revocation must discard already queued settings");
  assert.equal(browser.calls("UpdateDisplay").at(-1).payload.sent, null, "the late reply must not restore sent settings");
  browser.guest.updateState({ joined: true, ready: true });
  await setImmediate();
  assert.equal(browser.calls("SubmitSettings").length, 1, "regrant must not automatically send");
  assert.equal(autoSend.checked, false);
  browser.element('[data-input="speed"]').value = "30";
  browser.click("[data-send-button]");
  await setImmediate();
  assert.equal(browser.calls("SubmitSettings").length, 2);
  completeSubmission({ ok: true });
  await setImmediate();
  browser.guest.dispose();
});

test("late submission reply cannot restore the duplicate baseline after Stop", async () => {
  let completeSubmission;
  const browser = await createGuest(async (method) => {
    if (method === "SubmitSettings")
      return new Promise((resolve) => {
        completeSubmission = resolve;
      });
    return { ok: true };
  });
  browser.click("[data-send-button]");
  await setImmediate();
  browser.click("[data-stop-button]");
  await setImmediate();
  assert.equal(browser.calls("StopRun").length, 1, "Stop must bypass the pending settings submission");
  completeSubmission({ ok: true });
  await setImmediate();
  browser.guest.updateState({ joined: true, ready: true });
  browser.click("[data-send-button]");
  await setImmediate();
  assert.equal(browser.calls("SubmitSettings").length, 2);
  completeSubmission({ ok: true });
  await setImmediate();
  browser.guest.dispose();
});

test("known rejection remains retryable and successful duplicate is suppressed", async () => {
  let accepted = false;
  const browser = await createGuest(async () => ({ ok: accepted, code: "invalid-input" }));
  browser.click("[data-send-button]");
  await setImmediate();
  assert.equal(browser.element("[data-send-button]").disabled, false);
  accepted = true;
  browser.click("[data-send-button]");
  await setImmediate();
  browser.click("[data-send-button]");
  await setImmediate();
  assert.equal(browser.calls("SubmitSettings").length, 2);
  browser.guest.dispose();
});

test("interop failure persistently disconnects and later state cannot enable sends", async () => {
  const browser = await createGuest(async (method) => {
    if (method === "SubmitSettings") throw new Error("Disconnected");
    return { ok: true };
  });
  browser.click("[data-send-button]");
  await setImmediate();
  assert.equal(browser.storage.get(disconnectedKey), "true");
  assert.equal(browser.document.documentElement.dataset.guestConnectionLost, "true");
  assert.equal(browser.element(".preview-rail").paused, true);
  browser.guest.updateState({ joined: true, ready: true });
  browser.click("[data-send-button]");
  await setImmediate();
  assert.equal(browser.calls("SubmitSettings").length, 1);
  assert.equal(browser.element("[data-send-button]").disabled, true);
  browser.guest.dispose();
});

for (const event of ["popstate", "hashchange"]) {
  test(`${event} with a reopened invitation reloads before Blazor observes the URI`, async () => {
    const browser = await createGuest();
    assert.equal(browser.guest.takeInvitation(), "test-invitation");
    assert.equal(browser.location.hash, "");
    browser.location.hash = "#invite=reopened-invitation";
    const observed = [];
    browser.window.addEventListener(event, () => observed.push(browser.location.href));
    browser.window.dispatchEvent(new Event(event));
    assert.equal(observed.length, 0, "Blazor must not observe the capability URI");
    assert.equal(browser.reloads.length, 1);
    const reopened = await createGuest(undefined, browser.reloads[0]);
    assert.equal(reopened.location.hash, "");
    assert.equal(reopened.guest.takeInvitation(), "reopened-invitation");
    assert.equal(reopened.guest.takeInvitation(), null);
    browser.guest.dispose();
    reopened.guest.dispose();
  });
}

test("malformed invitation fragments are scrubbed and never redeemed", async () => {
  for (const fragment of ["invite=", "invite=one&invite=two", "invite=one&extra=two", `invite=${"a".repeat(129)}`, "other=value"]) {
    const browser = await createGuest(undefined, `https://guest.example/join#${fragment}`);
    assert.equal(browser.location.hash, "");
    assert.equal(browser.guest.takeInvitation(), null);
    browser.guest.dispose();
  }
});

test("a disposed controller's late failure cannot disconnect its replacement", async () => {
  let failSubmission;
  const browser = await createGuest(async (method) => {
    if (method === "SubmitSettings")
      return new Promise((_resolve, reject) => {
        failSubmission = reject;
      });
    return { ok: true };
  });
  browser.click("[data-send-button]");
  await setImmediate();
  browser.guest.attach(browser.root, { invokeMethodAsync: async () => ({ ok: true }) });
  browser.guest.updateState({ joined: true, ready: true });
  failSubmission(new Error("Old reference disposed"));
  await setImmediate();
  assert.equal(browser.storage.get(disconnectedKey), undefined);
  assert.equal(browser.element("[data-send-button]").disabled, false);
  browser.guest.dispose();
});
