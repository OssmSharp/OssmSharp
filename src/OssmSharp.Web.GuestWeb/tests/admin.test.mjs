import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { test } from "node:test";
import { createContext, SourceTextModule } from "node:vm";

const source = await readFile(new URL("../../OssmSharp.Web.AdminWeb/wwwroot/admin.js", import.meta.url), "utf8");

async function adminHarness(storage) {
  const context = createContext({ localStorage: storage });
  const module = new SourceTextModule(source, { context });
  await module.link(() => assert.fail("Unexpected import"));
  await module.evaluate();
  return { api: module.namespace };
}

test("saving attachment length preserves centimetres and surfaces unavailable storage", async () => {
  const stored = new Map();
  const failure = new Error("Storage unavailable.");
  const storage = {
    setItem(key, value) { stored.set(key, value); },
    getItem(key) { return stored.get(key) ?? null; },
  };
  const h = await adminHarness(storage);
  h.api.writeAttachmentLength(27.25);
  assert.equal(stored.get("ossm-admin-attachment-length-cm"), "27.25");
  assert.equal(h.api.readAttachmentLength(24), 27.25);
  storage.setItem = () => { throw failure; };
  assert.throws(() => h.api.writeAttachmentLength(28), (error) => error === failure);
  assert.equal(stored.get("ossm-admin-attachment-length-cm"), "27.25");
});
