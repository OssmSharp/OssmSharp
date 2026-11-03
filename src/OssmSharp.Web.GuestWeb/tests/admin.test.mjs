import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { test } from "node:test";
import { createContext, SourceTextModule } from "node:vm";

const source = await readFile(new URL("../../OssmSharp.Web.AdminWeb/wwwroot/admin.js", import.meta.url), "utf8");

async function adminHarness(clipboard) {
  const context = createContext({ navigator: { clipboard } });
  const module = new SourceTextModule(source, { context });
  await module.link(() => assert.fail("Unexpected import"));
  await module.evaluate();
  return module.namespace;
}

test("copy passes the exact invitation to the clipboard and confirms completion", async () => {
  const copied = [];
  const api = await adminHarness({ writeText: async (text) => copied.push(text) });
  const invitation = "https://guest.example/join#invite=isolated-invite";
  assert.equal(await api.copy(invitation), true);
  assert.deepEqual(copied, [invitation]);
});

test("copy returns failure when clipboard access is rejected or unavailable", async () => {
  for (const clipboard of [undefined, { writeText: async () => { throw new Error("Clipboard unavailable"); } }]) {
    const api = await adminHarness(clipboard);
    assert.equal(await api.copy("isolated-invite"), false);
  }
});
