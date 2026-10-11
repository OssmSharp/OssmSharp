import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { createContext, SourceTextModule } from "node:vm";

export const connectionSource = await readFile(new URL("../../../OssmSharp.Web.Common/wwwroot/connection-ui.js", import.meta.url), "utf8");
export const guestSource = await readFile(new URL("../../wwwroot/guest-session.js", import.meta.url), "utf8");

export function connectionHarness(extra = {}) {
  const observers = [];
  const classes = new Set(["components-reconnect-hide"]);
  const classChanges = [];
  const rail = { pauses: 0, pauseAnimations() { this.pauses++; } };
  const document = Object.assign(new EventTarget(), {
    body: {},
    documentElement: { dataset: {} },
    querySelectorAll: () => [rail],
    getElementById: () => ({
      classList: {
        remove: (value) => { classChanges.push(["remove", value]); classes.delete(value); },
        add: (value) => { classChanges.push(["add", value]); classes.add(value); },
      },
    }),
  });
  const context = createContext({
    document,
    MutationObserver: class {
      disconnected = false;
      constructor(callback) { this.callback = callback; observers.push(this); }
      observe(target, options) { this.target = target; this.options = options; this.disconnected = false; }
      disconnect() { this.disconnected = true; }
    },
    ...extra,
  });
  const reconnect = (state) => {
    const event = new Event("components-reconnect-state-changed");
    event.detail = { state };
    document.dispatchEvent(event);
  };
  return { context, document, observers, rail, classes, classChanges, reconnect };
}

export async function loadModule(source, context) {
  const module = new SourceTextModule(source, { context });
  await module.link(() => assert.fail("Unexpected import"));
  await module.evaluate();
  return module;
}

export async function attachGuest(h) {
  class Control extends EventTarget {
    value = "0";
    min = "0";
    max = "100";
    checked = false;
    disabled = false;
    attributes = new Map();
    dataset = {};
    style = { setProperty() {} };
    setAttribute(name, value) { this.attributes.set(name, String(value)); }
    getAttribute(name) { return this.attributes.get(name) ?? null; }
    removeAttribute(name) { this.attributes.delete(name); }
    toggleAttribute(name, enabled) { enabled ? this.setAttribute(name, "") : this.removeAttribute(name); }
  }
  const elements = new Map();
  for (const name of ["speed", "stroke", "depth", "sensation"]) {
    for (const kind of ["input", "value", "sent", "current", "sent-marker", "current-marker"])
      elements.set(`[data-${kind}="${name}"]`, new Control());
  }
  const patterns = Array.from({ length: 7 }, (_, pattern) => {
    const button = new Control();
    button.dataset.pattern = String(pattern);
    button.setAttribute("aria-pressed", String(pattern === 6));
    elements.set(`[data-pattern="${pattern}"]`, button);
    return button;
  });
  for (const selector of ["[data-send-button]", "[data-reset-position-button]", "[data-stop-button]", "[data-auto-send]", "[data-leave-button]"])
    elements.set(selector, new Control());
  for (const [name, value] of Object.entries({ speed: 35, stroke: 10, depth: 20, sensation: 75 }))
    elements.get(`[data-input="${name}"]`).value = String(value);
  const controls = [...elements].filter(([selector]) => /\[data-(input|pattern|.*button|auto-send)/.test(selector)).map(([, control]) => control);
  const storage = new Map();
  const root = new Control();
  root.querySelector = (selector) => elements.get(selector) ?? null;
  root.querySelectorAll = (selector) => selector === "[data-pattern]" ? patterns : [];
  h.document.querySelector = (selector) => selector === ".preview-rail" ? h.rail : root.querySelector(selector);
  h.document.querySelectorAll = (selector) => selector === ".preview-rail" ? [h.rail] : controls;
  Object.assign(h.context, {
    AbortController, URLSearchParams, window: new EventTarget(),
    location: new URL("https://guest.example/join"),
    history: { replaceState() {} },
    sessionStorage: {
      getItem: (key) => storage.get(key) ?? null,
      setItem: (key, value) => storage.set(key, value),
      removeItem: (key) => storage.delete(key),
    },
  });
  h.document.visibilityState = "visible";
  const module = await loadModule(guestSource, h.context);
  const calls = [];
  module.namespace.attach(root, { invokeMethodAsync: async (method, payload) => { calls.push({ method, payload }); return { ok: true }; } });
  module.namespace.updateState({ joined: true, ready: true });
  return { api: module.namespace, controls, storage, elements, calls };
}
