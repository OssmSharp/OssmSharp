import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { test } from "node:test";
import { createContext, SourceTextModule } from "node:vm";

const source = await readFile(new URL("../../OssmSharp.Web.AdminWeb/wwwroot/admin.js", import.meta.url), "utf8");

async function timerHarness(storage) {
  let now = Date.parse("2026-01-01T00:00:00Z");
  let element;
  let next = 0;
  const intervals = new Map();
  const context = createContext({
    document: { querySelector: () => element },
    localStorage: storage,
    Date: { parse: Date.parse, now: () => now },
    setInterval: (callback, milliseconds) => {
      assert.equal(milliseconds, 1000);
      intervals.set(++next, callback);
      return next;
    },
    clearInterval: (id) => intervals.delete(id),
  });
  const module = new SourceTextModule(source, { context });
  await module.link(() => assert.fail("Unexpected import"));
  await module.evaluate();
  return {
    api: module.namespace,
    intervals,
    setElement: (value) => { element = value; },
    tick: (elapsed) => {
      now = Date.parse("2026-01-01T00:00:00Z") + elapsed;
      for (const callback of [...intervals.values()]) callback();
    },
  };
}

test("duration formatting is exact and timestamp changes own one timer", async () => {
  const h = await timerHarness();
  h.api.updateConnectionTimer();
  assert.equal(h.intervals.size, 0);
  const element = { dataset: {}, isConnected: true };
  h.setElement(element);
  for (const timestamp of [undefined, "", "invalid"]) {
    element.dataset.connectedAt = timestamp;
    h.api.updateConnectionTimer();
    assert.equal(element.textContent, "Unknown");
    assert.equal(h.intervals.size, 0);
  }
  element.dataset.connectedAt = "2026-01-01T00:00:00Z";
  h.api.updateConnectionTimer();
  const interval = [...h.intervals.keys()][0];
  for (const [milliseconds, expected] of [
    [-1000, "00:00:00"], [999, "00:00:00"], [1000, "00:00:01"],
    [59_999, "00:00:59"], [60_000, "00:01:00"], [3_599_999, "00:59:59"],
    [3_661_000, "01:01:01"], [360_000_000, "100:00:00"],
  ]) {
    h.tick(milliseconds);
    assert.equal(element.textContent, expected);
    h.api.updateConnectionTimer();
    assert.deepEqual([...h.intervals.keys()], [interval]);
  }
  element.dataset.connectedAt = "2026-01-01T00:00:01Z";
  h.api.updateConnectionTimer();
  assert.equal(h.intervals.size, 1);
  assert.notEqual([...h.intervals.keys()][0], interval);
  h.api.disposeConnectionTimer();
  assert.equal(h.intervals.size, 0);
});

test("timer releases detached or disconnected elements and restarts on recovery", async () => {
  const h = await timerHarness();
  const timestamp = "2026-01-01T00:00:00Z";
  const element = { dataset: { connectedAt: timestamp }, isConnected: true };
  h.setElement(element);
  h.api.updateConnectionTimer();
  for (const timestamp of [undefined, ""]) {
    element.dataset.connectedAt = timestamp; // Rendered connecting/disconnected states omit the timestamp.
    h.tick(1000);
    assert.equal(h.intervals.size, 0);
    h.api.updateConnectionTimer();
    assert.equal(element.textContent, "Unknown");
    element.dataset.connectedAt = "2026-01-01T00:00:00Z";
    h.api.updateConnectionTimer();
    assert.equal(h.intervals.size, 1);
  }
  element.isConnected = false;
  h.tick(2000);
  assert.equal(h.intervals.size, 0);
  const replacement = { dataset: { connectedAt: timestamp }, isConnected: true };
  h.setElement(replacement);
  h.api.updateConnectionTimer();
  assert.equal(replacement.textContent, "00:00:02");
  assert.equal(h.intervals.size, 1);
  h.setElement(undefined);
  h.api.updateConnectionTimer();
  assert.equal(h.intervals.size, 0);
  h.setElement(replacement);
  h.api.updateConnectionTimer();
  assert.equal(h.intervals.size, 1);
  h.api.disposeConnectionTimer();
  h.api.disposeConnectionTimer();
  assert.equal(h.intervals.size, 0);
});

test("saving attachment length preserves centimetres and surfaces unavailable storage", async () => {
  const stored = new Map();
  const failure = new Error("Storage unavailable.");
  const storage = {
    setItem(key, value) { stored.set(key, value); },
    getItem(key) { return stored.get(key) ?? null; },
  };
  const h = await timerHarness(storage);
  h.api.writeAttachmentLength(27.25);
  assert.equal(stored.get("ossm-admin-attachment-length-cm"), "27.25");
  assert.equal(h.api.readAttachmentLength(24), 27.25);
  storage.setItem = () => { throw failure; };
  assert.throws(() => h.api.writeAttachmentLength(28), (error) => error === failure);
  assert.equal(stored.get("ossm-admin-attachment-length-cm"), "27.25");
});
