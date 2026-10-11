import assert from "node:assert/strict";
import { execFile } from "node:child_process";
import { once } from "node:events";
import { readFile } from "node:fs/promises";
import { request } from "node:http";
import { createServer } from "node:https";
import { createRequire } from "node:module";
import { join } from "node:path";
import { promisify } from "node:util";
import { createWebHostFixture, waitFor } from "./web-host-fixture.mjs";
import { readHubMessages } from "./protocol-transport.mjs";

export { waitFor };
export const accepted = { accepted: true, code: "accepted" };
export const settings = { pattern: 0, speed: 35, stroke: 10, depth: 20, sensation: 50 };

export function deviceState(overrides = {}) {
  return {
    pattern: 0, speed: 0, stroke: 10, depth: 20, sensation: 50,
    isArmed: true, isConnected: true, isConnecting: false, connectionFailed: false,
    isResettingPosition: false, name: null, identifier: null, connectedAt: null,
    transmittedSettings: null, ...overrides,
  };
}

export function guestState(overrides = {}) {
  return { joined: true, controlAllowed: true, controlExpiresAt: null, canResumeAfterStop: false, device: deviceState(), ...overrides };
}

export function userState(device = null) {
  return { invitation: { token: "isolated-admin-invite" }, guestJoined: true, guestConnected: true, invitationConsumed: false, guestControlAllowed: false, controlExpiresAt: null, device };
}

export async function launchBrowser() {
  const { chromium } = createRequire(process.env.PLAYWRIGHT_MODULE ?? import.meta.url)("playwright");
  return chromium.launch({
    headless: true, timeout: 15_000,
    executablePath: process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE,
    args: ["--host-resolver-rules=MAP guest.test 127.0.0.1", "--no-proxy-server"],
  });
}

function apiSession(state) {
  return {
    state, calls: [], requests: [], pending: new Map(), reads: 0, holdReads: false, failReads: false, holdStop: false,
    disconnectStops: 0, holdDisconnectStop: false,
  };
}

/** Browser-only collaborator options reuse the independent real HTTP host fixture. */
export async function createBrowserFixture(t, browser) {
  const guests = new Map();
  const admin = apiSession(userState());
  const fixture = await createWebHostFixture({
    api: {
      authorize: (authorization) => authorization !== "Bearer rejected-invite",
      admitted(saved) {
        const session = guests.get(saved.invitationToken);
        session.saved = saved;
        session.state = { ...session.state, sessionExpiresAt: saved.expiresAt };
      },
      detached(saved) {
        const session = guests.get(saved.invitationToken);
        session.disconnectStops++;
        saved.stopPending = session.holdDisconnectStop;
        session.state = {
          ...session.state, canResumeAfterStop: session.state.controlAllowed === true,
          device: session.state.device && { ...session.state.device, isArmed: false, speed: 0 },
        };
      },
      revoked(saved) {
        const session = guests.get(saved.invitationToken);
        session.state = { ...session.state, joined: false, controlAllowed: false };
      },
      invitationChanged(invitation) {
        admin.state = {
          ...admin.state, invitation: { token: invitation }, invitationConsumed: false,
          guestJoined: false, guestConnected: false, guestControlAllowed: false, guestSessionExpiresAt: null,
        };
      },
      invoke(call, { reply, reject }) {
        const session = call.session.authorization === `ServiceKey ${fixture.adminKey}`
          ? admin : guests.get(call.session.invitationToken);
        if (!session) throw new Error("Browser API call has no explicitly provisioned session");
        session.calls.push(call.target);
        if (["Redeem", "GetSavedSession", "Join", "LeaveSavedSession", "Leave"].includes(call.target)) return false;
        session.api = call.session;
        if (call.target === "GetGuestState" || call.target === "GetUserState") {
          session.reads++;
          if (session.failReads) reject();
          else if (session.holdReads) session.pending.set(call.target, reply);
          else reply(session.state);
        } else if (call.target === "SetPreviewDimensions") {
          assert.equal(session, admin, "Only the user can update preview dimensions");
          admin.state = { ...admin.state, preview: call.arguments[1] };
          for (const guest of guests.values()) guest.state = { ...guest.state, preview: call.arguments[1] };
          reply({ accepted: true, code: "preview-updated" });
        } else if (call.target === "Submit") {
          const submitted = call.arguments[0];
          session.requests.push(submitted);
          session.state = {
            ...session.state, canResumeAfterStop: false,
            device: { ...session.state.device, ...submitted, isArmed: true },
          };
          reply({ correlationId: submitted.correlationId, status: 0 });
        } else if (call.target === "GuestStop" && !session.holdStop) {
          session.state = {
            ...session.state, canResumeAfterStop: true,
            device: { ...session.state.device, isArmed: false, speed: 0 },
          };
          reply(accepted);
        } else if (["GuestStop", "GuestResetPosition", "Connect", "AllowGuest", "Stop", "EndGuest", "RefreshInvitation", "ResetPosition"].includes(call.target)) {
          assert.equal(session.pending.has(call.target), false, "A pending command cannot be duplicated");
          session.pending.set(call.target, reply);
        } else {
          throw new Error(`Unsupported browser API collaborator method: ${call.target}`);
        }
        return true;
      },
    },
  });
  const contexts = [];
  const proxies = [];
  t.after(async () => {
    try {
      for (const context of contexts) await context.close();
    } finally {
      try {
        for (const proxy of proxies) await proxy.dispose();
      } finally {
        await fixture.dispose();
        admin.pending.clear();
        for (const session of guests.values()) session.pending.clear();
        guests.clear();
      }
    }
    assert.deepEqual(fixture.faults, []);
    assert.equal(fixture.sockets.size, 0);
    assert.equal(fixture.listening, false);
    assert.ok(fixture.hosts.every(({ child }) => child.exitCode !== null || child.signalCode !== null));
  });
  let localGuest;
  let localAdmin;
  let publicGuest;
  const result = {
    fixture, admin,
    provision(invitation, state = guestState()) {
      assert.equal(guests.has(invitation), false);
      const session = apiSession(state);
      if (admin.state.preview) session.state = { ...session.state, preview: admin.state.preview };
      guests.set(invitation, session);
      return session;
    },
    async context(options = {}) {
      const context = await browser.newContext({ reducedMotion: "no-preference", ...options });
      contexts.push(context);
      return context;
    },
    async guestHost() {
      return localGuest ??= await fixture.startHost("guest", { settings: { OSSM__ControlClient__RequestTimeoutSeconds: "30" } });
    },
    async adminHost() {
      return localAdmin ??= await fixture.startHost("admin", { settings: { OSSM__ControlClient__RequestTimeoutSeconds: "30" } });
    },
    async publicHost() {
      if (!publicGuest) {
        const proxy = await createTlsProxy(fixture.home);
        proxies.push(proxy);
        const host = await fixture.startHost("guest", { publicOrigin: proxy.origin, settings: { OSSM__ControlClient__RequestTimeoutSeconds: "30" } });
        proxy.target = host.origin;
        publicGuest = { ...host, origin: proxy.origin, proxy };
      }
      return publicGuest;
    },
    async openGuest(invitation, { state = guestState(), ready = true, publicHost = false, context } = {}) {
      const session = guests.get(invitation) ?? result.provision(invitation, state);
      const host = publicHost ? await result.publicHost() : await result.guestHost();
      context ??= await result.context({ ignoreHTTPSErrors: publicHost });
      const view = await observePage(context);
      await view.page.goto(`${host.origin}/join#invite=${invitation}`);
      await until(() => view.page.getByRole("button", { name: "Send", exact: true }).isDisabled(), !ready, "guest readiness");
      assert.equal(new URL(view.page.url()).hash, "");
      return { ...view, context, host, session };
    },
    async openAdmin(state = userState()) {
      admin.state = state;
      const host = await result.adminHost();
      const context = await result.context();
      const view = await observePage(context);
      await view.page.goto(host.origin);
      await view.page.getByTestId("device-connection").waitFor({ state: "visible" });
      return { ...view, context, host, session: admin };
    },
  };
  return result;
}

export async function observePage(context) {
  const page = await context.newPage();
  await page.route("**/favicon.ico", (route) => route.fulfill({ status: 204 }));
  page.setDefaultTimeout(7_000);
  const errors = [];
  const sentFrames = [];
  const navigationFrames = [];
  const navigationUrls = [];
  const renderFrames = [];
  page.on("pageerror", (error) => errors.push(error.message));
  page.on("console", (message) => { if (message.type() === "error") errors.push(message.text()); });
  page.on("request", (request) => { if (request.isNavigationRequest()) navigationUrls.push(request.url()); });
  page.on("websocket", (socket) => socket.on("framesent", ({ payload }) => sentFrames.push(Buffer.from(payload))));
  // Playwright's frame events omit the opcode: close/ping bytes are not hub messages.
  const transport = await context.newCDPSession(page);
  await transport.send("Network.enable");
  for (const [event, received] of [["Network.webSocketFrameSent", false], ["Network.webSocketFrameReceived", true]]) {
    const partial = new Map();
    const handshaken = new Set();
    transport.on(event, ({ requestId, response }) => {
      if (response.opcode !== 1 && response.opcode !== 2) return;
      let payload = response.opcode === 1
        ? Buffer.from(response.payloadData) : Buffer.from(response.payloadData, "base64");
      // SignalR sends its JSON handshake before switching to binary hub envelopes.
      if (received && !handshaken.has(requestId)) {
        const separator = payload.indexOf(0x1e);
        assert.ok(separator >= 0, "SignalR handshake response required");
        assert.equal(JSON.parse(payload.subarray(0, separator).toString()).error, undefined);
        handshaken.add(requestId);
        payload = payload.subarray(separator + 1);
      } else if (response.opcode === 1) return;
      const decoded = readHubMessages(Buffer.concat([
        partial.get(requestId) ?? Buffer.alloc(0), payload,
      ]));
      partial.set(requestId, decoded.rest);
      for (const message of decoded.messages) {
        if (message[0] !== 1) continue;
        if (received && message[3] === "JS.RenderBatch") renderFrames.push(message);
        if (!received && /(?:StartCircuit|Location)/.test(message[3])) navigationFrames.push(message);
      }
    });
    transport.on("Network.webSocketClosed", ({ requestId }) => {
      partial.delete(requestId);
      handshaken.delete(requestId);
    });
  }
  return { page, errors, sentFrames, navigationFrames, navigationUrls, renderFrames };
}

/** Polls asynchronous semantic locators; failures remain visible with a bounded deadline. */
export async function until(read, expected, description, milliseconds = 7_000) {
  const deadline = Date.now() + milliseconds;
  let actual;
  do {
    actual = await read();
    if (typeof expected === "function" ? expected(actual) : Object.is(actual, expected)) return actual;
    await new Promise((resolve) => setTimeout(resolve, 20));
  } while (Date.now() < deadline);
  assert.fail(`Deadline waiting for ${description}; last value: ${JSON.stringify(actual)}`);
}

export function button(page, name) {
  return page.getByRole("button", { name, exact: true });
}

export function preview(page) {
  return page.getByRole("img", { name: / preview at / });
}

export async function setRange(page, name, value) {
  await page.getByLabel(name, { exact: true }).evaluate((input, value) => {
    input.value = String(value);
    input.dispatchEvent(new Event("input", { bubbles: true }));
  }, value);
}

export async function complete(session, command, result = accepted) {
  await waitFor(() => session.pending.has(command), `${command} RPC`);
  session.pending.get(command)(result);
  session.pending.delete(command);
}

export async function sent(page, name, value) {
  await until(() => page.getByLabel(name, { exact: true }).getAttribute("aria-valuetext"),
    (text) => text?.includes(`Sent value: ${value}.`), `${name} sent value`);
}

export function assertCapabilityAbsent(view, tokens, serviceKey) {
  assert.ok(view.navigationFrames.some((message) => message[3] === "StartCircuit"), "Real circuit startup must be inspected");
  for (const frame of view.sentFrames) {
    assert.equal(frame.includes("#invite="), false);
    assert.equal(frame.includes(serviceKey), false);
  }
  // Invitations cross the HTTP redemption boundary, never circuit interop or navigation.
  for (const frame of view.sentFrames)
    for (const token of tokens) assert.equal(frame.includes(token), false);
  for (const message of view.navigationFrames) {
    const frame = JSON.stringify(message);
    for (const token of tokens) assert.equal(frame.includes(token), false);
  }
  for (const url of view.navigationUrls) {
    assert.equal(url.includes("#invite="), false);
    for (const token of tokens) assert.equal(url.includes(token), false);
  }
}

export async function injectFatal(page) {
  await page.evaluate(() => { document.getElementById("blazor-error-ui").style.display = "block"; });
}

async function createTlsProxy(home) {
  const key = join(home, "browser-proxy.key");
  const cert = join(home, "browser-proxy.pem");
  await promisify(execFile)("openssl", [
    "req", "-x509", "-newkey", "rsa:2048", "-nodes", "-days", "1", "-subj", "/CN=guest.test", "-keyout", key, "-out", cert,
  ], { timeout: 10_000, maxBuffer: 64_000 });
  const sockets = new Set();
  const upstreams = new Set();
  const proxy = { target: null };
  const options = (incoming) => ({ method: incoming.method, headers: { ...incoming.headers, "x-forwarded-proto": "https" } });
  function upstream(incoming) {
    const outgoing = request(new URL(incoming.url, proxy.target), options(incoming));
    upstreams.add(outgoing);
    outgoing.on("close", () => upstreams.delete(outgoing));
    return outgoing;
  }
  const server = createServer({ key: await readFile(key), cert: await readFile(cert) }, (incoming, response) => {
    const outgoing = upstream(incoming);
    outgoing.on("response", (reply) => { response.writeHead(reply.statusCode, reply.headers); reply.pipe(response); });
    outgoing.on("error", () => response.destroy());
    incoming.pipe(outgoing);
  });
  server.on("connection", (socket) => {
    sockets.add(socket);
    socket.on("close", () => sockets.delete(socket));
  });
  server.on("upgrade", (incoming, socket, head) => {
    const outgoing = upstream(incoming);
    outgoing.on("upgrade", (reply, connection, incomingHead) => {
      sockets.add(connection);
      connection.on("close", () => sockets.delete(connection));
      socket.write(`HTTP/1.1 ${reply.statusCode} ${reply.statusMessage}\r\n`);
      for (let i = 0; i < reply.rawHeaders.length; i += 2) socket.write(`${reply.rawHeaders[i]}: ${reply.rawHeaders[i + 1]}\r\n`);
      socket.write("\r\n");
      if (incomingHead.length) socket.write(incomingHead);
      if (head.length) connection.write(head);
      connection.on("error", () => socket.destroy());
      socket.on("error", () => connection.destroy());
      socket.on("close", () => connection.destroy());
      connection.pipe(socket).pipe(connection);
    });
    outgoing.on("response", (reply) => {
      reply.resume();
      socket.end(`HTTP/1.1 ${reply.statusCode} ${reply.statusMessage}\r\nConnection: close\r\nContent-Length: 0\r\n\r\n`);
    });
    outgoing.on("error", () => socket.destroy());
    outgoing.end();
  });
  server.listen(0, "127.0.0.1");
  await once(server, "listening");
  proxy.origin = `https://guest.test:${server.address().port}`;
  proxy.disconnect = () => { for (const socket of sockets) socket.destroy(); };
  proxy.dispose = async () => {
    for (const outgoing of upstreams) outgoing.destroy();
    const closed = [...sockets].map((socket) => new Promise((resolve) => socket.once("close", resolve)));
    proxy.disconnect();
    await Promise.all(closed);
    await new Promise((resolve, reject) => server.close((error) => error ? reject(error) : resolve()));
    assert.equal(sockets.size, 0);
    assert.equal(server.listening, false);
  };
  return proxy;
}
