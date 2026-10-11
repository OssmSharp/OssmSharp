import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { test } from "node:test";
import { SourceTextModule } from "node:vm";
import { connectionHarness, connectionSource, guestSource } from "./fixtures/connection-harness.mjs";

const source = await readFile(new URL("../wwwroot/guest-start.js", import.meta.url), "utf8");
const authSource = await readFile(new URL("../wwwroot/guest-auth.js", import.meta.url), "utf8");
const ready = { code: "ready", requestToken: "csrf", connected: false, canAttach: true };

async function bootstrap(failure, responses = [ready]) {
  const location = new URL("https://guest.example/join#invite=synthetic-invitation");
  const storage = new Map();
  const order = [];
  const errors = [];
  const h = connectionHarness({
    URL, URLSearchParams, AbortController, location,
    reportError: (error) => errors.push(error.message),
    window: new EventTarget(),
    sessionStorage: {
      getItem: (key) => storage.get(key) ?? null,
      setItem: (key, value) => storage.set(key, value),
      removeItem: (key) => storage.delete(key),
    },
    history: { replaceState: (_state, _title, path) => { location.href = new URL(path, location).href; order.push("scrub"); } },
    fetch: async (path, options) => {
      order.push(options.method ?? "GET");
      assert.equal(location.hash, "");
      const body = responses.shift();
      assert.notEqual(body, undefined, `Unexpected request to ${path}`);
      return { ok: true, status: 200, json: async () => body };
    },
    setTimeout: () => assert.fail("Unexpected background recovery"),
    clearTimeout() {},
    Blazor: {
      start: async (options) => {
        order.push("start");
        assert.equal(location.hash, "");
        assert.equal(options.circuit.reconnectionOptions.maxRetries, 0);
        assert.equal(typeof options.circuit.reconnectionHandler.onConnectionDown, "function");
        assert.equal(typeof options.circuit.reconnectionHandler.onConnectionUp, "function");
        if (failure === "start") throw new Error("Startup rejected");
      },
    },
  });
  location.reload = () => order.push("reload");
  h.document.baseURI = location.origin + "/join";
  const modal = h.document.getElementById();
  h.document.getElementById = (id) => id === "guest-start" ? { dataset: { controller: "/guest-session.js" } } : modal;
  h.document.querySelector = () => null;
  h.document.querySelectorAll = () => [];
  const connection = new SourceTextModule(connectionSource, { context: h.context });
  await connection.link(() => assert.fail("Unexpected connection import"));
  const guest = new SourceTextModule(guestSource, { context: h.context });
  await guest.link(() => assert.fail("Unexpected guest import"));
  const auth = new SourceTextModule(authSource, { context: h.context });
  await auth.link(() => assert.fail("Unexpected authentication import"));
  const start = new SourceTextModule(source, {
    context: h.context,
    importModuleDynamically: async (url) => {
      order.push("import");
      assert.equal(url, "https://guest.example/guest-session.js");
      if (failure === "import") throw new Error("Import failed");
      await guest.evaluate();
      return guest;
    },
  });
  await start.link((specifier) => {
    if (specifier === "./guest-auth.js") return auth;
    assert.equal(specifier, "./_content/OssmSharp.Web.Common/connection-ui.js");
    return connection;
  });
  await start.evaluate();
  return { ...h, guest, storage, order, location, errors };
}

test("invitation scrubbing and saved-cookie validation precede Blazor startup", async () => {
  const h = await bootstrap();
  assert.deepEqual(h.order, ["import", "scrub", "GET", "start"]);
  assert.equal(h.location.href, "https://guest.example/join");
  assert.equal(h.guest.namespace.takeInvitation(), null);
  assert.equal(h.guest.namespace.getSessionCode(), "ready");
  assert.equal(h.document.documentElement.dataset.connectionLost, undefined);
  h.guest.namespace.dispose();
});

test("a newly redeemed cookie is confirmed and reloaded before starting an authenticated circuit", async () => {
  const h = await bootstrap(undefined, [{ code: "no-session", requestToken: "csrf" }, { code: "saved" }, ready]);
  assert.deepEqual(h.order, ["import", "scrub", "GET", "POST", "GET", "reload"]);
  assert.equal(h.location.hash, "");
  assert.equal(h.guest.namespace.takeInvitation(), null);
  h.guest.namespace.dispose();
});

for (const failure of ["import", "start"]) {
  test(`${failure} failure uses one fallback without fabricating session state`, async () => {
    const h = await bootstrap(failure);
    assert.equal(h.document.documentElement.dataset.connectionLost, "true");
    assert.equal(h.classes.has("components-reconnect-rejected"), true);
    assert.equal(h.observers[0].disconnected, true);
    assert.equal(h.storage.get("ossm-guest-connection-lost"), undefined);
    assert.equal(h.document.documentElement.dataset.joined, undefined);
    assert.equal(h.document.documentElement.dataset.invalid, undefined);
    assert.deepEqual(h.errors, ["Guest page initialization failed."]);
    h.reconnect("failed");
    assert.equal(h.observers[0].disconnected, true);
    assert.deepEqual(h.classChanges, [
      ["remove", "components-reconnect-hide"], ["add", "components-reconnect-rejected"],
    ]);
    if (failure === "start") {
      assert.equal(h.guest.namespace.takeInvitation(), null);
      assert.deepEqual(h.order, ["import", "scrub", "GET", "start"]);
      h.guest.namespace.dispose();
    } else assert.deepEqual(h.order, ["import"]);
  });
}
