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
  for (const [name, value] of Object.entries({ speed: 30, stroke: 10, depth: 20, sensation: 50 }))
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

test("all seven pattern selections survive snapshots and reach preview and requests", async () => {
  const browser = await createGuest();
  for (let pattern = 0; pattern <= 6; pattern++) {
    const input = browser.element("[data-pattern]");
    input.value = String(pattern);
    input.dispatchEvent(new Event("change"));
    browser.guest.updateState({ joined: true, ready: true, current: { pattern: 6 - pattern } });
    browser.click("[data-send-button]");
    await setImmediate();
    assert.equal(browser.calls("SubmitSettings").at(-1).payload.pattern, pattern);
    const display = browser.calls("UpdateDisplay").at(-1).payload;
    assert.equal(display.selected.pattern, pattern);
    assert.equal(display.sent.pattern, pattern);
    assert.equal(input.value, String(pattern));
  }
  browser.guest.dispose();
});

test("speed is sent unchanged and retains the selected speed", async () => {
  const browser = await createGuest();
  for (const [speed, stroke, depth] of [
    [50, 10, 10],
    [50, 80, 100],
    [50, 10, 80],
    [53, 10, 20],
    [100, 100, 100],
    [0, 10, 20],
    [30, 1, 1],
    [100, 1, 1],
    [50, 0, 20],
    [50, 0, 0],
  ]) {
    for (const [name, value] of Object.entries({ depth, stroke, speed })) {
      const input = browser.element(`[data-input="${name}"]`);
      input.value = String(value);
      input.dispatchEvent(new Event("input"));
      assert.equal(browser.element(`[data-value="${name}"]`).textContent, value);
    }
    const count = browser.calls("SubmitSettings").length;
    browser.click("[data-send-button]");
    await setImmediate();
    const command = browser.calls("SubmitSettings").at(-1).payload;
    assert.deepEqual([command.speed, command.stroke, command.depth], [speed, stroke, depth]);
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

test("Reset Position preserves selection, discards stale sends, and allows independent Stop", async () => {
  let completeRequest;
  let completeReset;
  const browser = await createGuest(async (method) => {
    if (method === "SubmitSettings") return new Promise((resolve) => { completeRequest = resolve; });
    if (method === "ResetPosition") return new Promise((resolve) => { completeReset = resolve; });
    return { ok: true };
  });
  const auto = browser.element("[data-auto-send]");
  auto.checked = true;
  auto.dispatchEvent(new Event("change"));
  await setImmediate();
  browser.element('[data-input="speed"]').value = "40";
  browser.element('[data-input="speed"]').dispatchEvent(new Event("input"));
  browser.click("[data-reset-position-button]");
  await setImmediate();
  assert.equal(browser.calls("ResetPosition").length, 1);
  assert.equal(auto.checked, false);
  assert.equal(browser.element('[data-input="speed"]').value, "40");
  assert.equal(browser.element("[data-send-button]").disabled, true);
  assert.equal(browser.calls("UpdateDisplay").at(-1).payload.previewStopped, true);
  completeRequest({ ok: true });
  await setImmediate();
  assert.equal(browser.calls("SubmitSettings").length, 1);
  assert.equal(browser.calls("UpdateDisplay").at(-1).payload.sent, null);
  browser.click("[data-stop-button]");
  await setImmediate();
  assert.equal(browser.calls("StopRun").length, 1);
  completeReset({ ok: false, code: "cancelled" });
  await setImmediate();
  assert.equal(browser.calls("UpdateDisplay").at(-1).payload.error, null, "late reset cancellation cannot replace successful Stop");
  assert.equal(browser.element("[data-send-button]").disabled, false, "successful concurrent Stop must not require a second Stop");
  browser.guest.dispose();
});

test("Reset Position completion and remote reset keep preview paused until deliberate Send", async () => {
  const browser = await createGuest();
  browser.click("[data-reset-position-button]");
  await setImmediate();
  assert.equal(browser.calls("ResetPosition").length, 1);
  assert.equal(browser.element('[data-input="speed"]').value, "30");
  browser.guest.updateState({ joined: true, ready: true, stopped: true });
  await setImmediate();
  assert.equal(browser.calls("SubmitSettings").length, 0);
  assert.equal(browser.calls("UpdateDisplay").at(-1).payload.previewStopped, true);
  browser.click("[data-send-button]");
  await setImmediate();
  assert.equal(browser.calls("UpdateDisplay").at(-1).payload.previewStopped, false);
  browser.guest.updateState({ joined: true, ready: true, resettingPosition: true });
  await setImmediate();
  assert.equal(browser.element("[data-send-button]").disabled, true);
  assert.equal(browser.calls("UpdateDisplay").at(-1).payload.previewStopped, true);
  browser.guest.updateState({ joined: true, ready: true, stopped: true });
  await setImmediate();
  assert.equal(browser.element('[data-input="speed"]').value, "30");
  assert.equal(browser.calls("SubmitSettings").length, 1);
  browser.guest.updateState({ joined: true, ready: false });
  browser.click("[data-reset-position-button]");
  await setImmediate();
  assert.equal(browser.calls("ResetPosition").length, 1, "an unapproved guest cannot reset position");
  browser.guest.dispose();
});

test("positive device recovery resumes the selected preview without sending settings", async () => {
  const browser = await createGuest();
  browser.guest.updateState({ joined: true, ready: false });
  await setImmediate();
  assert.equal(browser.calls("UpdateDisplay").at(-1).payload.previewStopped, true);
  assert.equal(browser.element('[data-input="speed"]').value, "0");

  browser.guest.updateState({ joined: true, ready: true });
  await setImmediate();
  assert.equal(browser.calls("UpdateDisplay").at(-1).payload.previewStopped, false);
  assert.equal(browser.calls("SubmitSettings").length, 0);
  assert.equal(browser.element("[data-send-button]").disabled, false);
  browser.guest.dispose();
});

test("stale settings timeout cannot erase reset selection or hide reset failure", async () => {
  let completeRequest;
  let completeReset;
  const browser = await createGuest(async (method) => {
    if (method === "SubmitSettings") return new Promise((resolve) => { completeRequest = resolve; });
    if (method === "ResetPosition") return new Promise((resolve) => { completeReset = resolve; });
    return { ok: true };
  });
  browser.click("[data-send-button]");
  await setImmediate();
  browser.click("[data-reset-position-button]");
  completeRequest({ ok: false, code: "outcome-unknown" });
  await setImmediate();
  assert.equal(browser.element('[data-input="speed"]').value, "30");
  assert.equal(browser.calls("UpdateDisplay").at(-1).payload.sent, null);
  completeReset({ ok: false, code: "device-io-failed" });
  await setImmediate();
  assert.equal(browser.calls("UpdateDisplay").at(-1).payload.error, "Reset Position failed (device-io-failed). Use Stop before sending again.");
  browser.guest.updateState({ joined: true, ready: true });
  browser.click("[data-send-button]");
  await setImmediate();
  assert.equal(browser.calls("SubmitSettings").length, 1, "a failed reset stays paused until Stop succeeds");
  browser.click("[data-stop-button]");
  await setImmediate();
  assert.equal(browser.element("[data-send-button]").disabled, false);
  browser.guest.dispose();
});

test("preview display skips intermediate slider edits while an update is in flight", async () => {
  let completeDisplay;
  const browser = await createGuest(async (method) => {
    if (method === "UpdateDisplay")
      return new Promise((resolve) => {
        completeDisplay = resolve;
      });
    return { ok: true };
  });
  const input = browser.element('[data-input="depth"]');
  for (let depth = 21; depth <= 80; depth++) {
    input.value = String(depth);
    input.dispatchEvent(new Event("input"));
  }
  assert.equal(browser.element('[data-value="depth"]').textContent, 80);
  assert.equal(browser.calls("UpdateDisplay").length, 1);
  completeDisplay();
  await setImmediate();
  assert.equal(browser.calls("UpdateDisplay").length, 2);
  assert.equal(browser.calls("UpdateDisplay").at(-1).payload.selected.depth, 80, "the preview must receive the latest selection, not replay depth 21");
  assert.equal(browser.calls("SubmitSettings").length, 0, "preview edits must not send device commands");
  input.value = "81";
  input.dispatchEvent(new Event("input"));
  browser.guest.dispose();
  completeDisplay();
  await setImmediate();
  assert.equal(browser.calls("UpdateDisplay").length, 2, "disposal must discard pending display work");
});

test("pattern and sensation supersession keeps the latest preview after a snapshot", async () => {
  let completeDisplay;
  const browser = await createGuest(async (method) => method === "UpdateDisplay"
    ? new Promise((resolve) => { completeDisplay = resolve; }) : { ok: true });
  for (let pattern = 0; pattern <= 6; pattern++) {
    browser.element("[data-pattern]").value = String(pattern);
    browser.element("[data-pattern]").dispatchEvent(new Event("change"));
    browser.element('[data-input="sensation"]').value = String(pattern * 10);
    browser.element('[data-input="sensation"]').dispatchEvent(new Event("input"));
  }
  browser.guest.updateState({ joined: true, ready: true, current: { pattern: 0, sensation: 0 } });
  assert.equal(browser.calls("UpdateDisplay").length, 1);
  completeDisplay();
  await setImmediate();
  const selected = browser.calls("UpdateDisplay").at(-1).payload.selected;
  assert.deepEqual([selected.pattern, selected.sensation], [6, 60]);
  assert.equal(browser.calls("UpdateDisplay").length, 2);
  assert.equal(browser.calls("SubmitSettings").length, 0);
  browser.guest.dispose();
  completeDisplay();
});

test("unknown outcome pauses queued and new sends until an acknowledged recovery Stop", async () => {
  let completeRequest;
  let completeStop;
  const browser = await createGuest(async (method) => {
    if (method === "SubmitSettings")
      return new Promise((resolve) => {
        completeRequest = resolve;
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
  completeRequest({ ok: false, code: "outcome-unknown" });
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
  completeRequest({ ok: true });
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

test("control revocation cancels queued auto sends and regrant requires deliberate request", async () => {
  let completeRequest;
  const browser = await createGuest(async (method) => {
    if (method === "SubmitSettings")
      return new Promise((resolve) => {
        completeRequest = resolve;
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
  assert.equal(browser.element('[data-input="speed"]').value, "0");
  assert.equal(browser.element("[data-send-button]").disabled, true);
  completeRequest({ ok: true });
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
  completeRequest({ ok: true });
  await setImmediate();
  browser.guest.dispose();
});

test("focus loss and a hidden tab retain motion and selection until page exit", async () => {
  const browser = await createGuest();
  browser.click("[data-send-button]");
  await setImmediate();
  browser.window.dispatchEvent(new Event("blur"));
  browser.document.visibilityState = "hidden";
  browser.document.dispatchEvent(new Event("visibilitychange"));
  await setImmediate();
  assert.equal(browser.calls("StopRun").length, 0);
  assert.equal(browser.element('[data-input="speed"]').value, "30");
  assert.equal(browser.calls("UpdateDisplay").at(-1).payload.sent.speed, 30);
  browser.window.dispatchEvent(new Event("pagehide"));
  await setImmediate();
  assert.equal(browser.calls("StopRun").length, 1);
  assert.equal(browser.element('[data-input="speed"]').value, "0");
  browser.guest.dispose();
});

test("late request reply cannot restore the duplicate baseline after Stop", async () => {
  let completeRequest;
  const browser = await createGuest(async (method) => {
    if (method === "SubmitSettings")
      return new Promise((resolve) => {
        completeRequest = resolve;
      });
    return { ok: true };
  });
  browser.click("[data-send-button]");
  await setImmediate();
  browser.click("[data-stop-button]");
  await setImmediate();
  assert.equal(browser.calls("StopRun").length, 1, "Stop must bypass the pending settings request");
  completeRequest({ ok: true });
  await setImmediate();
  browser.guest.updateState({ joined: true, ready: true });
  browser.click("[data-send-button]");
  await setImmediate();
  assert.equal(browser.calls("SubmitSettings").length, 2);
  completeRequest({ ok: true });
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
  assert.equal(browser.element('[data-input="speed"]').value, "0");
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
  let failRequest;
  const browser = await createGuest(async (method) => {
    if (method === "SubmitSettings")
      return new Promise((_resolve, reject) => {
        failRequest = reject;
      });
    return { ok: true };
  });
  browser.click("[data-send-button]");
  await setImmediate();
  browser.guest.attach(browser.root, { invokeMethodAsync: async () => ({ ok: true }) });
  browser.guest.updateState({ joined: true, ready: true });
  failRequest(new Error("Old reference disposed"));
  await setImmediate();
  assert.equal(browser.storage.get(disconnectedKey), undefined);
  assert.equal(browser.element("[data-send-button]").disabled, false);
  browser.guest.dispose();
});

// Static DOM inventory for the transferred disconnect guarantees: unknown selectors return null.
async function createObservedGuest({ storage = new Map(), url = "https://guest.example/join", ready = true } = {}) {
  const elements = new Map();
  for (const name of ["speed", "stroke", "depth", "sensation"]) {
    for (const kind of ["input", "value", "sent", "current", "sent-marker", "current-marker"])
      elements.set(`[data-${kind}="${name}"]`, new Element());
  }
  for (const selector of ["[data-pattern]", "[data-send-button]", "[data-reset-position-button]", "[data-stop-button]", "[data-auto-send]",
    "[data-leave-button]", ".preview-rail", "main > [data-testid='guest-disconnected']"])
    elements.set(selector, new Element());
  for (const [name, value] of Object.entries({ speed: 30, stroke: 10, depth: 20, sensation: 50 }))
    elements.get(`[data-input="${name}"]`).value = String(value);
  const controls = () => [...elements].filter(([selector]) => /\[data-(input|pattern|.*button|auto-send)/.test(selector)).map(([, element]) => element);
  const root = new Element();
  root.querySelector = (selector) => elements.get(selector) ?? null;
  const document = Object.assign(new EventTarget(), {
    body: {}, visibilityState: "visible", documentElement: new Element(),
    querySelector: root.querySelector, querySelectorAll: controls,
  });
  const window = new EventTarget();
  const location = new URL(url);
  const observers = [];
  const calls = [];
  const context = createContext({
    AbortController, URLSearchParams, document, window, location,
    history: { replaceState: (_state, _title, path) => { location.href = new URL(path, location).href; } },
    sessionStorage: {
      getItem: (key) => storage.get(key) ?? null,
      setItem: (key, value) => storage.set(key, value),
      removeItem: (key) => storage.delete(key),
    },
    MutationObserver: class {
      constructor(callback) { this.callback = callback; observers.push(this); }
      disconnect() { this.active = false; }
      observe(target, options) { this.active = true; this.target = target; this.options = options; }
    },
  });
  const module = new SourceTextModule(source, { context });
  await module.link(() => assert.fail("Unexpected import"));
  await module.evaluate();
  const guest = module.namespace;
  const reference = { invokeMethodAsync: async (method, payload) => { calls.push({ method, payload }); return { ok: true }; } };
  guest.attach(root, reference);
  guest.updateState({ joined: true, ready });
  await setImmediate();
  return { guest, root, elements, controls, document, storage, observers, reference, calls, element: root.querySelector };
}

test("all seven patterns reach manual and auto requests without snapshot replacement", async () => {
  for (const automatic of [false, true]) {
    const browser = await createGuest();
    browser.element('[data-input="speed"]').value = "50";
    browser.element('[data-input="speed"]').dispatchEvent(new Event("input"));
    const sensation = browser.element('[data-input="sensation"]');
    sensation.value = "75";
    sensation.dispatchEvent(new Event("input"));
    const toggle = browser.element("[data-auto-send]");
    toggle.checked = automatic; // Pattern change, not enabling the toggle, owns each request below.
    for (let pattern = 0; pattern <= 6; pattern++) {
      const count = browser.calls("SubmitSettings").length;
      const input = browser.element("[data-pattern]");
      input.value = String(pattern);
      input.dispatchEvent(new Event("change"));
      browser.guest.updateState({ joined: true, ready: true, current: { pattern: (pattern + 1) % 7 } });
      if (!automatic) browser.click("[data-send-button]");
      await setImmediate();
      assert.equal(browser.calls("SubmitSettings").length, count + 1);
      const payload = browser.calls("SubmitSettings").at(-1).payload;
      assert.deepEqual([payload.pattern, payload.sensation, payload.speed], [pattern, 75, 50]);
      const display = browser.calls("UpdateDisplay").at(-1).payload;
      assert.deepEqual([display.selected.pattern, display.sent.pattern, display.selected.sensation], [pattern, pattern, 75]);
      assert.equal(input.value, String(pattern));
    }
    browser.guest.dispose();
  }
});

test("requested values remain normalized while current observations stay exact", async () => {
  const browser = await createGuest();
  // Full old browser request table; the original ten controller rows above remain intact.
  for (const [speed, stroke, depth, currentSpeed] of [
    [50, 10, 10, 50], [50, 80, 100, 50], [50, 10, 80, 50], [53, 10, 10, 50],
    [25, 10, 10, 20], [100, 100, 100, 100], [0, 10, 20, 0], [30, 1, 1, 0], [100, 1, 1, 100],
  ]) {
    for (const [name, value] of Object.entries({ depth, stroke, speed })) {
      const input = browser.element(`[data-input="${name}"]`);
      input.value = String(value);
      input.dispatchEvent(new Event("input"));
      assert.equal(browser.element(`[data-value="${name}"]`).textContent, value);
    }
    const count = browser.calls("SubmitSettings").length;
    browser.click("[data-send-button]");
    await setImmediate();
    const sent = browser.calls("SubmitSettings").at(-1).payload;
    assert.deepEqual([sent.speed, sent.stroke, sent.depth], [speed, stroke, depth]);
    browser.guest.updateState({ joined: true, ready: true, current: { speed: currentSpeed, stroke } });
    assert.equal(browser.element('[data-current="speed"]').textContent, currentSpeed);
    browser.click("[data-send-button]");
    await setImmediate();
    assert.equal(browser.calls("SubmitSettings").length, count + 1);
  }
  for (const [speed, stroke] of [[50, 10], [12.5, 20], [33.333333333333336, 30], [null, 10], [null, 0], [50, null], [0, 10], [400, 10]]) {
    browser.guest.updateState({ joined: true, ready: true, current: { speed, stroke } });
    assert.equal(browser.element('[data-current="speed"]').textContent, speed ?? "--");
    const marker = browser.element('[data-current-marker="speed"]');
    assert.equal(marker.attributes.has("hidden"), speed === null);
    assert.equal(browser.element('[data-input="speed"]').attributes.get("aria-valuetext"),
      `Selected value: 100. Sent value: 100. Current value: ${speed ?? "unavailable"}.`);
    browser.element('[data-input="stroke"]').value = "1";
    browser.element('[data-input="stroke"]').dispatchEvent(new Event("input"));
    assert.equal(browser.element('[data-current="speed"]').textContent, speed ?? "--");
  }
  for (const [value, expected] of [[-1, 0], [0, 0], [100, 100], [101, 100]]) {
    for (const name of ["speed", "sensation", "depth", "stroke"]) {
      browser.element(`[data-input="${name}"]`).value = String(value);
      browser.element(`[data-input="${name}"]`).dispatchEvent(new Event("input"));
    }
    for (const name of ["speed", "sensation", "depth", "stroke"])
      assert.equal(browser.element(`[data-input="${name}"]`).value, String(expected));
    browser.click("[data-send-button]");
    await setImmediate();
    const request = browser.calls("SubmitSettings").at(-1).payload;
    assert.deepEqual([request.speed, request.sensation, request.depth, request.stroke], [expected, expected, expected, expected]);
  }
  browser.element('[data-input="depth"]').value = "20";
  browser.element('[data-input="stroke"]').value = "21";
  browser.element('[data-input="stroke"]').dispatchEvent(new Event("input"));
  assert.equal(browser.element('[data-input="stroke"]').value, "20");
  browser.guest.dispose();
});

test("permission and device transitions disarm without replay or false transport loss", async () => {
  // Ready is a controller input; the authoritative device predicate is separately tested in JoinComponentTests.
  for (const initiallyReady of [false, true]) {
    const browser = await createObservedGuest({ ready: initiallyReady });
    for (const device of [null, { isConnected: false }, { isConnecting: true }, { connectionFailed: true }]) {
      browser.element('[data-input="speed"]').value = "35";
      browser.element("[data-auto-send]").checked = true;
      browser.guest.updateState({ joined: true, ready: false, current: device });
      assert.equal(browser.element('[data-input="speed"]').value, "0");
      assert.equal(browser.element("[data-auto-send]").checked, false);
      assert.equal(browser.element("[data-send-button]").disabled, true);
      browser.element("[data-send-button]").dispatchEvent(new Event("click"));
      browser.guest.updateState({ joined: true, ready: true });
      await setImmediate();
      assert.equal(browser.calls.filter(({ method }) => method === "SubmitSettings" || method === "Renew").length, 0);
      assert.equal(browser.calls.at(-1).payload.previewStopped, true);
      assert.equal(browser.storage.get(disconnectedKey), undefined);
      assert.equal(browser.document.documentElement.dataset.guestConnectionLost, undefined);
    }
    browser.guest.dispose();
  }
});

test("ended state differs from persistent polling and transport failure", async () => {
  const ended = await createObservedGuest();
  ended.guest.updateState({ joined: false, ready: false });
  ended.element("[data-send-button]").dispatchEvent(new Event("click"));
  await setImmediate();
  assert.equal(ended.element("[data-send-button]").disabled, true);
  assert.equal(ended.element('[data-input="speed"]').value, "0");
  assert.equal(ended.storage.get(disconnectedKey), undefined);
  assert.equal(ended.calls.filter(({ method }) => method === "SubmitSettings" || method === "Renew").length, 0);
  ended.guest.dispose();
  // Poll result classification is Join's responsibility; disconnect is its actual JS endpoint.
  const lost = await createObservedGuest();
  lost.guest.disconnect();
  lost.guest.updateState({ joined: true, ready: true });
  lost.element("[data-send-button]").dispatchEvent(new Event("click"));
  await setImmediate();
  assert.equal(lost.storage.get(disconnectedKey), "true");
  assert.equal(lost.element('[data-input="speed"]').value, "0");
  assert.equal(lost.controls().every((control) => control.disabled), true);
  assert.equal(lost.calls.filter(({ method }) => method === "SubmitSettings" || method === "Renew").length, 0);
  lost.guest.dispose();
});

test("disconnect survives replacement markup reload and stale references", async () => {
  const browser = await createObservedGuest();
  browser.guest.disconnect();
  assert.equal(browser.element(".preview-rail").paused, true);
  assert.equal(browser.observers[0].active, true);
  assert.equal(browser.observers[0].target, browser.document.body);
  const replacement = new Element();
  browser.elements.set("[data-leave-button]", replacement);
  browser.observers[0].callback([]);
  assert.equal(replacement.disabled, true);
  browser.guest.attach(browser.root, browser.reference);
  browser.guest.updateState({ joined: true, ready: true });
  assert.equal(browser.controls().every((control) => control.disabled), true);
  const reload = await createObservedGuest({ storage: browser.storage });
  assert.equal(reload.element("[data-send-button]").disabled, true);
  assert.equal(reload.document.documentElement.dataset.guestConnectionLost, "true");
  assert.equal(reload.guest.takeInvitation(), null);
  browser.guest.dispose();
  reload.guest.dispose();
  // The persistence observer deliberately survives dispose to protect replacement markup.
  assert.equal(browser.observers[0].active, true);
});

test("same-document invitation links are intercepted before Blazor navigation", async () => {
  for (const hash of ["#invite=reopened", "#invite=one&invite=two"]) {
    const browser = await createGuest();
    const link = { tagName: "A", origin: browser.location.origin, pathname: "/join", hash };
    const event = new Event("click");
    event.composedPath = () => [{ tagName: "SPAN" }, link];
    let exposed = false;
    browser.document.addEventListener("click", () => { exposed = true; });
    browser.document.dispatchEvent(event);
    assert.equal(exposed, false, "Blazor's later navigation listener must not see the invitation link");
    // Native link navigation remains allowed: the destination document captures and scrubs.
    assert.equal(event.defaultPrevented, false);
    const reopened = await createGuest(undefined, browser.location.origin + "/join" + hash);
    assert.equal(reopened.location.hash, "");
    assert.equal(reopened.guest.takeInvitation(), hash.includes("&") ? null : "reopened");
    assert.equal(reopened.guest.takeInvitation(), null);
    browser.guest.dispose();
    reopened.guest.dispose();
  }
});

test("preview supersession delivers exactly depths 21 and 80 without device requests", async () => {
  let complete;
  let gated = false;
  const browser = await createGuest(async (method) => {
    if (method === "UpdateDisplay" && gated) return new Promise((resolve) => { complete = resolve; });
    return { ok: true };
  });
  const baseline = browser.calls("UpdateDisplay").length;
  gated = true;
  const depth = browser.element('[data-input="depth"]');
  for (let value = 21; value <= 80; value++) {
    depth.value = String(value);
    depth.dispatchEvent(new Event("input"));
  }
  assert.equal(browser.element('[data-value="depth"]').textContent, 80);
  assert.deepEqual(browser.calls("UpdateDisplay").slice(baseline).map(({ payload }) => payload.selected.depth), [21]);
  complete();
  await setImmediate();
  assert.deepEqual(browser.calls("UpdateDisplay").slice(baseline).map(({ payload }) => payload.selected.depth), [21, 80]);
  assert.equal(browser.calls("SubmitSettings").length, 0);
  browser.guest.dispose();
  complete();
  await setImmediate();
});

test("Reset Position preserves speed 35 and pattern 6 with no completion replay", async () => {
  const browser = await createGuest();
  browser.element('[data-input="speed"]').value = "35";
  browser.element("[data-pattern]").value = "6";
  browser.element("[data-auto-send]").checked = true;
  browser.element("[data-auto-send]").dispatchEvent(new Event("change"));
  await setImmediate();
  browser.click("[data-reset-position-button]");
  await setImmediate();
  browser.guest.updateState({ joined: true, ready: true, stopped: true });
  await setImmediate();
  assert.equal(browser.calls("ResetPosition").length, 1);
  assert.equal(browser.calls("SubmitSettings").length, 1);
  assert.equal(browser.element('[data-input="speed"]').value, "35");
  assert.equal(browser.element("[data-pattern]").value, "6");
  assert.equal(browser.element("[data-auto-send]").checked, false);
  assert.equal(browser.calls("UpdateDisplay").at(-1).payload.previewStopped, true);
  browser.guest.dispose();
});
