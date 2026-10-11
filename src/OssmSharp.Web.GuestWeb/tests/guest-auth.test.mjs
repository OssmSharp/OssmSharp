import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { setImmediate } from "node:timers/promises";
import { test } from "node:test";
import { createContext, SourceTextModule } from "node:vm";

const source = await readFile(new URL("../wwwroot/guest-auth.js", import.meta.url), "utf8");
const ready = { code: "ready", requestToken: "csrf", connected: false, canAttach: true };
const missing = { code: "no-session", requestToken: "csrf" };

async function sessionHarness(responses) {
  const calls = [];
  const states = [];
  const timers = new Map();
  const delays = [];
  const reloads = [];
  const errors = [];
  const window = new EventTarget();
  let timerId = 0;
  const context = createContext({
    AbortController, window,
    reportError: (error) => errors.push(error.message),
    location: { reload: () => reloads.push(true) },
    fetch: async (path, options) => {
      calls.push({ path, options });
      const response = responses.shift();
      assert.notEqual(response, undefined, "Unexpected HTTP request");
      if (response instanceof Error) throw response;
      if (typeof response === "function") return response(options.signal);
      const [status, body] = Array.isArray(response) ? response : [200, response];
      return { ok: status >= 200 && status < 300, status, json: async () => body };
    },
    setTimeout: (callback, delay) => {
      const id = ++timerId;
      timers.set(id, callback);
      delays.push(delay);
      return id;
    },
    clearTimeout: (id) => timers.delete(id),
  });
  const module = new SourceTextModule(source, { context });
  await module.link(() => assert.fail("Unexpected authentication dependency"));
  await module.evaluate();
  const session = module.namespace.createSession((state) => states.push(state));
  return {
    session, calls, states, timers, delays, reloads, window, errors,
    async tick() {
      const [id, callback] = timers.entries().next().value;
      timers.delete(id);
      callback();
      await setImmediate();
    },
  };
}

test("saved authority takes precedence over a supplied invitation", async () => {
  const h = await sessionHarness([ready]);
  assert.equal(await h.session.prepare("unused-invitation"), "ready");
  assert.deepEqual(h.calls.map((call) => call.path), ["/guest-session"]);
  assert.equal(h.calls[0].options.credentials, "same-origin");
  assert.equal(h.calls[0].options.cache, "no-store");
});

test("redemption is single-use and cookie confirmation precedes a fresh circuit", async () => {
  const h = await sessionHarness([missing, { code: "saved" }, ready]);
  assert.equal(await h.session.prepare("single-use-invitation"), "reload");
  assert.deepEqual(h.calls.map((call) => call.path), ["/guest-session", "/guest-session/redeem", "/guest-session"]);
  const request = h.calls[1].options;
  assert.equal(request.method, "POST");
  assert.equal(request.credentials, "same-origin");
  assert.equal(request.headers["X-CSRF-TOKEN"], "csrf");
  assert.deepEqual(JSON.parse(request.body), { token: "single-use-invitation" });
});

test("a browser that did not retain its cookie cannot appear joined", async () => {
  const h = await sessionHarness([missing, { code: "saved" }, missing]);
  assert.equal(await h.session.prepare("invitation"), "cookie-unavailable");
  assert.equal(h.calls.filter((call) => call.path.endsWith("/redeem")).length, 1);
});

test("a lost redemption response reconciles a saved cookie without redeeming twice", async () => {
  const h = await sessionHarness([missing, new TypeError("Lost response"), ready]);
  assert.equal(await h.session.prepare("invitation"), "reload");
  assert.equal(h.calls.filter((call) => call.path.endsWith("/redeem")).length, 1);
});

test("a lost response without a cookie remains an unknown result", async () => {
  const h = await sessionHarness([missing, new TypeError("Lost response"), missing]);
  assert.equal(await h.session.prepare("invitation"), "redemption-unknown");
  assert.equal(h.calls.length, 3);
});

test("an unavailable proxy is retryable even without a JSON body or antiforgery token", async () => {
  const h = await sessionHarness([[503, null]]);
  assert.equal(await h.session.prepare(null), "connection-unavailable");
});

test("an invalid bootstrap response is terminal rather than retried as a connection failure", async () => {
  const invalid = [502, { code: "invalid-response", requestToken: "csrf" }];
  const h = await sessionHarness([invalid]);
  h.session.recover();
  await setImmediate();
  assert.deepEqual(h.states, ["invalid-response"]);
  assert.equal(h.timers.size, 0);
  h.session.recover();
  await setImmediate();
  assert.equal(h.calls.length, 1);
  assert.equal(h.reloads.length, 0);
  h.session.end();
});

test("an invalid bootstrap response cannot trigger invitation redemption", async () => {
  const h = await sessionHarness([[502, { code: "invalid-response", requestToken: "csrf" }]]);
  assert.equal(await h.session.prepare("invitation"), "invalid-response");
  assert.equal(h.calls.length, 1);
});

test("a proxy-generated non-JSON 502 remains a retryable connection failure", async () => {
  const h = await sessionHarness([
    () => ({ status: 502, ok: false, json: async () => { throw new SyntaxError("Proxy response"); } }),
  ]);
  assert.equal(await h.session.prepare(null), "connection-unavailable");
  assert.deepEqual(h.errors, []);
});

test("a connection lost while reading bootstrap data retries and then recovers", async () => {
  const h = await sessionHarness([
    () => ({ status: 200, ok: true, json: async () => { throw new TypeError("terminated"); } }),
    ready,
  ]);
  h.session.recover();
  await setImmediate();
  assert.deepEqual(h.states, ["reconnecting"]);
  assert.deepEqual(h.errors, []);
  assert.deepEqual(h.delays, [1000]);
  await h.tick();
  assert.equal(h.reloads.length, 1);
  assert.equal(h.calls.length, 2);
  h.session.end();
});

test("malformed bootstrap JSON remains terminal rather than retried", async () => {
  const h = await sessionHarness([
    () => ({ status: 200, ok: true, json: async () => { throw new SyntaxError("Invalid JSON"); } }),
  ]);
  h.session.recover();
  await setImmediate();
  assert.deepEqual(h.states, ["recovery-failed"]);
  assert.equal(h.timers.size, 0);
  h.session.recover();
  assert.equal(h.calls.length, 1);
});

test("closing a page during a bootstrap body read cancels without retry or error", async () => {
  const h = await sessionHarness([
    signal => ({
      status: 200, ok: true,
      json: () => new Promise((_resolve, reject) => signal.addEventListener("abort", () => reject(signal.reason), { once: true })),
    }),
  ]);
  h.session.recover();
  await setImmediate();
  h.window.dispatchEvent(new Event("pagehide"));
  await setImmediate();
  assert.equal(h.calls.length, 1);
  assert.equal(h.reloads.length, 0);
  assert.equal(h.timers.size, 0);
  assert.deepEqual(h.states, []);
  assert.deepEqual(h.errors, []);
});

test("unexpected recovery failures produce a terminal notice and credential-free diagnostics", async () => {
  const h = await sessionHarness([new Error("Sensitive response content")]);
  h.session.recover();
  await setImmediate();
  assert.deepEqual(h.states, ["recovery-failed"]);
  assert.deepEqual(h.errors, ["Guest session recovery failed unexpectedly."]);
  assert.equal(h.timers.size, 0);
  h.session.recover();
  await setImmediate();
  assert.equal(h.calls.length, 1);
});

test("a used saved session waits for the connected tab instead of attaching or redeeming", async () => {
  const h = await sessionHarness([{ ...ready, connected: true, canAttach: false }]);
  assert.equal(await h.session.prepare("invitation"), "session-in-use");
  assert.equal(h.calls.length, 1);
});

test("a saved session waits for required Stop cleanup before attaching", async () => {
  const h = await sessionHarness([{ ...ready, canAttach: false }]);
  assert.equal(await h.session.prepare(null), "stop-pending");
});

test("recovery owns one bounded retry flow and reloads only after safe availability", async () => {
  const h = await sessionHarness([
    [503, { code: "connection-unavailable", requestToken: "csrf" }],
    { ...ready, connected: true, canAttach: false },
    { ...ready, canAttach: false },
    ready,
  ]);
  h.session.recover();
  h.session.recover();
  await setImmediate();
  assert.deepEqual(h.states, ["reconnecting"]);
  assert.deepEqual(h.delays, [1000]);
  assert.equal(h.reloads.length, 0);
  await h.tick();
  assert.equal(h.states.at(-1), "connection-detaching");
  assert.equal(h.delays.at(-1), 2000);
  await h.tick();
  assert.equal(h.states.at(-1), "stop-pending");
  assert.equal(h.delays.at(-1), 4000);
  await h.tick();
  assert.deepEqual(h.reloads, [true]);
  assert.equal(h.timers.size, 0);
  assert.ok(h.calls.every((call) => call.path === "/guest-session"));
});

test("recovery waits at most five seconds between later attempts", async () => {
  const unavailable = [503, { code: "connection-unavailable", requestToken: "csrf" }];
  const h = await sessionHarness([unavailable, unavailable, unavailable, unavailable, unavailable]);
  h.session.recover();
  await setImmediate();
  await h.tick();
  await h.tick();
  await h.tick();
  await h.tick();
  assert.deepEqual(h.delays, [1000, 2000, 4000, 5000, 5000]);
  h.session.end("session-ended");
  await setImmediate();
  assert.equal(h.timers.size, 0);
  assert.equal(h.reloads.length, 0);
});

test("Leave cancels recovery and revokes saved admission without a controller attachment", async () => {
  const h = await sessionHarness([{ ...ready, canAttach: false }, ready, { accepted: true, code: "guest-left" }]);
  h.session.recover();
  await setImmediate();
  const reply = await h.session.leave();
  await setImmediate();
  assert.equal(reply.ok, true);
  assert.equal(reply.code, "guest-left");
  assert.equal(h.timers.size, 0);
  const leave = h.calls.at(-1);
  assert.equal(leave.path, "/guest-session/leave");
  assert.equal(leave.options.method, "POST");
  assert.equal(leave.options.headers["X-CSRF-TOKEN"], "csrf");
  h.session.recover();
  await setImmediate();
  assert.equal(h.reloads.length, 0);
  assert.equal(h.calls.length, 3);
});

test("offline Leave rejected by a newly attached controller resumes waiting without replay", async () => {
  const h = await sessionHarness([
    ready,
    [409, { accepted: false, code: "guest-already-joined" }],
    { ...ready, connected: true, canAttach: false },
    ready,
  ]);
  const reply = await h.session.leave();
  await setImmediate();
  assert.equal(reply.ok, false);
  assert.equal(reply.code, "guest-already-joined");
  assert.deepEqual(h.states, ["session-in-use"]);
  await h.tick();
  assert.equal(h.reloads.length, 1);
  assert.equal(h.calls.filter(call => call.path.endsWith("/leave")).length, 1);
  h.session.end();
});

test("clearing the cookie after controller Leave cannot issue another Leave request", async () => {
  const h = await sessionHarness([ready, { code: "cookie-cleared" }]);
  await h.session.prepare(null);
  const reply = await h.session.clear();
  assert.equal(reply.ok, true);
  assert.deepEqual(h.calls.map(call => call.path), ["/guest-session", "/guest-session/clear"]);
  assert.equal(h.calls[1].options.headers["X-CSRF-TOKEN"], "csrf");
  h.session.recover();
  assert.equal(h.calls.length, 2);
});

test("a failed cookie clear remains an explicit failure without replay or recovery", async () => {
  const h = await sessionHarness([ready, new TypeError("Connection lost")]);
  await h.session.prepare(null);
  const reply = await h.session.clear();
  assert.equal(reply.ok, false);
  assert.equal(reply.code, "connection-unavailable");
  h.session.recover();
  assert.equal(h.calls.length, 2);
  assert.equal(h.timers.size, 0);
});

test("closing a page cancels an in-flight check without issuing a command or Leave", async () => {
  const h = await sessionHarness([
    (signal) => new Promise((_resolve, reject) => signal.addEventListener("abort", () => reject(signal.reason), { once: true })),
  ]);
  h.session.recover();
  h.window.dispatchEvent(new Event("pagehide"));
  await setImmediate();
  assert.equal(h.calls[0].options.signal.aborted, true);
  assert.equal(h.calls.length, 1);
  assert.equal(h.reloads.length, 0);
});

test("revocation is terminal and cannot be undone by another recovery trigger", async () => {
  const h = await sessionHarness([[401, { code: "invalid-guest-session", requestToken: "csrf" }]]);
  h.session.recover();
  await setImmediate();
  assert.deepEqual(h.states, ["invalid-guest-session"]);
  h.session.recover();
  await setImmediate();
  assert.equal(h.calls.length, 1);
  assert.equal(h.timers.size, 0);
  assert.equal(h.reloads.length, 0);
});

test("malformed API metadata is surfaced rather than treated as an available session", async () => {
  const h = await sessionHarness([{ code: "ready", requestToken: "csrf", connected: "false", canAttach: true }]);
  await assert.rejects(h.session.prepare(null), /Invalid guest session availability/);
});

test("contradictory availability cannot attach or steal a connected session", async () => {
  const h = await sessionHarness([{ ...ready, connected: true, canAttach: true }]);
  await assert.rejects(h.session.prepare(null), /Invalid guest session availability/);
  assert.equal(h.reloads.length, 0);
});
