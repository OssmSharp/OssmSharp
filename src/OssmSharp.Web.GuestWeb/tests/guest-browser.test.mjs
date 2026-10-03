import assert from "node:assert/strict";
import { execFile, spawn } from "node:child_process";
import { once } from "node:events";
import { mkdtemp, readFile, rm } from "node:fs/promises";
import { createServer, request as httpRequest } from "node:http";
import { createServer as createHttpsServer } from "node:https";
import { createRequire } from "node:module";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import { after, before, test } from "node:test";
import { fileURLToPath } from "node:url";
import { promisify } from "node:util";

const require = createRequire(process.env.PLAYWRIGHT_MODULE ?? import.meta.url);
const { chromium } = require("playwright");
const { wsServer: WebSocketServer } = require(join(dirname(require.resolve("playwright-core/package.json")), "lib/utilsBundle.js"));
const project = fileURLToPath(new URL("../", import.meta.url));
const artifact = fileURLToPath(new URL("../../../.artifacts/bin/ossmsharp.web.guestweb/debug/OssmSharp.Web.GuestWeb.dll", import.meta.url));
const adminProject = fileURLToPath(new URL("../../OssmSharp.Web.AdminWeb/", import.meta.url));
const adminArtifact = fileURLToPath(new URL("../../../.artifacts/bin/ossmsharp.web.adminweb/debug/OssmSharp.Web.AdminWeb.dll", import.meta.url));
const invitations = [];
const handshakes = [];
const submissions = [];
const viewports = [
  { width: 1280, height: 800 },
  { width: 390, height: 844 },
];
const sessions = new Map();
const hosts = [];
let browser;
let origin;
let adminOrigin;
let adminSession;
let home;
let peer;
let sockets;
let proxy;
let publicOrigin;
let proxyOrigin;
const proxySockets = new Set();

function guestState(overrides = {}) {
  return {
    joined: true,
    controlAllowed: true,
    controlExpiresAt: null,
    canResumeAfterStop: false,
    device: { pattern: 0, speed: 0, stroke: 10, depth: 20, sensation: 50, isArmed: true, connectionFailed: false, isConnected: true },
    ...overrides,
  };
}

function deviceState(overrides = {}) {
  return {
    pattern: 0,
    speed: 93,
    stroke: 91,
    depth: 89,
    sensation: 87,
    isArmed: true,
    connectionFailed: false,
    isConnected: false,
    isConnecting: false,
    name: null,
    identifier: null,
    connectedAt: null,
    transmittedSettings: null,
    ...overrides,
  };
}

function userState(device = null) {
  return {
    invitation: { token: "isolated-admin-invite" },
    guestJoined: true,
    guestControlAllowed: false,
    controlExpiresAt: null,
    device,
  };
}

function newAdminSession(state = userState()) {
  return {
    state,
    reads: 0,
    calls: [],
    pending: new Map(),
    received: new Map(["Connect", "AllowGuest", "Stop", "EndGuest"].map((name) => [name, Promise.withResolvers()])),
  };
}

async function listen(server) {
  server.listen(0, "127.0.0.1");
  await once(server, "listening");
  return server.address().port;
}

async function startHost(projectPath, assembly, settings) {
  const host = spawn("dotnet", [assembly], {
    cwd: projectPath,
    env: {
      ...process.env,
      HOME: home,
      DOTNET_CLI_HOME: home,
      ASPNETCORE_ENVIRONMENT: "Development",
      OTEL_SDK_DISABLED: "true",
      OSSM__ControlClient__ControlOrigin: `http://127.0.0.1:${peer.address().port}`,
      OSSM__ControlClient__ServiceKey: "A".repeat(43),
      ...settings,
    },
    stdio: ["ignore", "pipe", "pipe"],
  });
  hosts.push({ host, exit: once(host, "exit") });
  await new Promise((resolve, reject) => {
    const deadline = setTimeout(() => reject(new Error("UI host did not start")), 15_000);
    host.once("exit", (code) => {
      clearTimeout(deadline);
      reject(new Error(`UI host exited with ${code}`));
    });
    host.once("error", (error) => {
      clearTimeout(deadline);
      reject(error);
    });
    host.stdout.on("data", (data) => {
      if (data.toString().includes("Now listening on:")) {
        clearTimeout(deadline);
        resolve();
      }
    });
    host.stderr.on("data", (data) => process.stderr.write(data));
  });
}

async function getAdminOrigin() {
  if (adminOrigin) return adminOrigin;
  const reservation = createServer();
  const adminPort = await listen(reservation);
  await new Promise((resolve) => reservation.close(resolve));
  adminOrigin = `http://127.0.0.1:${adminPort}`;
  await startHost(adminProject, adminArtifact, {
    OSSM__Admin__AdminPort: String(adminPort),
    OSSM__Admin__GuestHostname: origin,
    OSSM__ControlClient__CommandTimeoutSeconds: "30",
  });
  return adminOrigin;
}

before(async () => {
  peer = createServer((request, response) => {
    if (request.url.startsWith("/hubs/control/v2/negotiate")) {
      handshakes.push(request.headers.authorization);
      if (request.headers.authorization === "Bearer rejected-invite") {
        response.writeHead(401).end();
        return;
      }
      response.writeHead(200, { "Content-Type": "application/json" });
      response.end(
        JSON.stringify({
          negotiateVersion: 1,
          connectionId: "test",
          connectionToken: "test",
          availableTransports: [{ transport: "WebSockets", transferFormats: ["Text", "Binary"] }],
        }),
      );
    } else {
      response.writeHead(404).end();
    }
  });
  sockets = new WebSocketServer({ server: peer });
  sockets.on("connection", (socket, request) => {
    handshakes.push(request.headers.authorization);
    let session;
    socket.on("close", () => session?.closed?.resolve());
    socket.on("message", (data) => {
      for (const text of data.toString().split("\u001e").filter(Boolean)) {
        const message = JSON.parse(text);
        if (message.protocol) {
          socket.send("{}\u001e");
        } else if (message.type === 6) {
          socket.send('{"type":6}\u001e');
        } else if (message.type === 1) {
          let result = { accepted: true, code: "accepted" };
          const reply = (value = result) => socket.send(`${JSON.stringify({ type: 3, invocationId: message.invocationId, result: value })}\u001e`);
          if (adminSession && ["Connect", "AllowGuest", "Stop", "EndGuest"].includes(message.target)) {
            adminSession.calls.push(message.target);
            adminSession.pending.set(message.target, reply);
            adminSession.received.get(message.target).resolve();
            continue;
          }
          if (message.target === "GetUserState") {
            if (adminSession) adminSession.reads++;
            result = adminSession?.state ?? userState();
          }
          if (message.target === "Join") {
            invitations.push(message.arguments[0].invitationToken);
            session = sessions.get(message.arguments[0].invitationToken);
          }
          if (message.target === "GetGuestState") {
            if (session?.pollFailure === "outcome-unknown") continue;
            if (session?.pollFailure === "connection-unavailable") {
              socket.close(1000);
              continue;
            }
            if (session?.pollFailure === "unexpected-rejection") {
              socket.send(`${JSON.stringify({ type: 3, invocationId: message.invocationId, error: "Isolated polling failure" })}\u001e`);
              continue;
            }
            result = session?.state ?? guestState();
          }
          if (message.target === "Renew" && session) {
            session.renewals++;
          }
          if (message.target === "GuestStop" && session) {
            session.stops++;
            session.stopReceived?.();
            session.completeStop = (value = { accepted: true, code: "accepted" }) => {
              if (value.accepted) {
                session.state = { ...session.state, canResumeAfterStop: true, device: { ...session.state.device, isArmed: false, speed: 0 } };
              }
              reply(value);
            };
            if (session.holdStop) continue;
            session.completeStop();
            continue;
          }
          if (message.target === "Submit") {
            submissions.push(message.arguments[0]);
            result = { correlationId: message.arguments[0].correlationId, status: 0 };
            if (session) {
              session.submissions.push(message.arguments[0]);
              session.state = {
                ...session.state,
                canResumeAfterStop: false,
                device: { ...session.state.device, isArmed: true, ...message.arguments[0] },
              };
            }
          }
          reply(result);
        }
      }
    });
  });
  await listen(peer);
  const reservation = createServer();
  const guestPort = await listen(reservation);
  await new Promise((resolve) => reservation.close(resolve));
  origin = `http://127.0.0.1:${guestPort}`;
  home = await mkdtemp(join(tmpdir(), "ossm-guest-test-"));
  await startHost(project, artifact, { OSSM__Guest__GuestPort: String(guestPort), OSSM__ControlClient__ServiceKey: "" });
  browser = await chromium.launch({
    headless: true,
    executablePath: process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE,
    args: ["--host-resolver-rules=MAP guest.test 127.0.0.1", "--no-proxy-server"],
  });
});

after(async () => {
  try {
    await browser?.close();
  } finally {
    for (const { host, exit } of hosts) {
      if (host.exitCode !== null) continue;
      const deadline = setTimeout(() => host.kill("SIGKILL"), 5_000);
      host.kill("SIGTERM");
      await exit;
      clearTimeout(deadline);
    }
    for (const socket of sockets?.clients ?? []) socket.terminate();
    for (const socket of proxySockets) socket.destroy();
    if (proxy) await new Promise((resolve) => proxy.close(resolve));
    if (sockets) await new Promise((resolve) => sockets.close(resolve));
    if (peer) await new Promise((resolve) => peer.close(resolve));
    if (home) await rm(home, { recursive: true, force: true });
  }
});

async function openGuest(context, invitation, ready = true, guestOrigin = origin) {
  const page = await context.newPage();
  page.setDefaultTimeout(10_000);
  const frames = [];
  const errors = [];
  page.on("pageerror", (error) => errors.push(error.message));
  page.on("console", (message) => {
    if (message.type() === "error") errors.push(message.text());
  });
  page.on("websocket", (socket) => socket.on("framesent", ({ payload }) => frames.push(Buffer.from(payload))));
  await page.goto(`${guestOrigin}/join#invite=${invitation}`);
  await page.waitForFunction((expected) => document.querySelector("[data-send-button]")?.disabled === !expected, ready);
  assert.equal(new URL(page.url()).hash, "");
  return { page, frames, errors };
}

async function getPublicOrigin() {
  if (publicOrigin) return publicOrigin;
  const key = join(home, "proxy.key");
  const cert = join(home, "proxy.pem");
  await promisify(execFile)("openssl", [
    "req",
    "-x509",
    "-newkey",
    "rsa:2048",
    "-nodes",
    "-days",
    "1",
    "-subj",
    "/CN=127.0.0.1",
    "-keyout",
    key,
    "-out",
    cert,
  ]);
  const reservation = createServer();
  const guestPort = await listen(reservation);
  await new Promise((resolve) => reservation.close(resolve));
  const target = `http://127.0.0.1:${guestPort}`;
  const options = (request) => ({ method: request.method, headers: { ...request.headers, "x-forwarded-proto": "https" } });
  proxy = createHttpsServer({ key: await readFile(key), cert: await readFile(cert) }, (request, response) => {
    const upstream = httpRequest(new URL(request.url, target), options(request), (incoming) => {
      response.writeHead(incoming.statusCode, incoming.headers);
      incoming.pipe(response);
    });
    upstream.on("error", () => response.destroy());
    request.pipe(upstream);
  });
  proxy.on("connection", (socket) => {
    proxySockets.add(socket);
    socket.on("close", () => proxySockets.delete(socket));
  });
  proxy.on("upgrade", (request, socket, head) => {
    const upstream = httpRequest(new URL(request.url, target), options(request));
    upstream.on("upgrade", (response, connection, incomingHead) => {
      socket.write(`HTTP/1.1 ${response.statusCode} ${response.statusMessage}\r\n`);
      for (let index = 0; index < response.rawHeaders.length; index += 2) {
        socket.write(`${response.rawHeaders[index]}: ${response.rawHeaders[index + 1]}\r\n`);
      }
      socket.write("\r\n");
      if (incomingHead.length) socket.write(incomingHead);
      if (head.length) connection.write(head);
      connection.on("error", () => socket.destroy());
      socket.on("error", () => connection.destroy());
      socket.on("close", () => connection.destroy());
      connection.pipe(socket).pipe(connection);
    });
    upstream.on("response", (response) => {
      response.resume();
      socket.end(`HTTP/1.1 ${response.statusCode} ${response.statusMessage}\r\nConnection: close\r\nContent-Length: 0\r\n\r\n`);
    });
    upstream.on("error", () => socket.destroy());
    upstream.end();
  });
  const port = await listen(proxy);
  proxyOrigin = `https://127.0.0.1:${port}`;
  publicOrigin = `https://guest.test:${port}`;
  await startHost(project, artifact, {
    OSSM__Guest__GuestPort: String(guestPort),
    OSSM__Guest__Hostname: publicOrigin,
    OSSM__ControlClient__ServiceKey: "",
  });
  return publicOrigin;
}

test("HTTPS reverse proxy carries guest circuits and invitation-authenticated API traffic", async () => {
  const externalOrigin = await getPublicOrigin();
  const context = await browser.newContext({ ignoreHTTPSErrors: true });
  try {
    const start = handshakes.length;
    const { page, frames, errors } = await openGuest(context, "public-test-invite", true, externalOrigin);
    assert.deepEqual(handshakes.slice(start), ["Bearer public-test-invite", "Bearer public-test-invite"]);
    assert.equal(
      frames.some((frame) => frame.includes("#invite=")),
      false,
    );
    const response = await page.request.get(`${proxyOrigin}/join`, { headers: { Host: new URL(externalOrigin).host } });
    assert.match(response.headers()["content-security-policy"], new RegExp(`wss://${new URL(externalOrigin).host}`));
    assert.equal(response.headers()["referrer-policy"], "no-referrer");
    assert.equal(response.headers()["x-frame-options"], "DENY");
    const submitted = submissions.length;
    await page.locator("[data-send-button]").click();
    await page.waitForFunction(() => document.querySelector("[data-send-status]")?.textContent.includes("selection sent"));
    assert.equal(submissions.length, submitted + 1);
    await page.getByRole("button", { name: "Leave session" }).click();
    await page.getByTestId("guest-ended").waitFor({ state: "visible" });
    assert.deepEqual(errors, []);
  } finally {
    await context.close();
  }
});

test("public reverse proxy rejects mismatched and missing origins, including WebSocket upgrades", async () => {
  const externalOrigin = await getPublicOrigin();
  const context = await browser.newContext({ ignoreHTTPSErrors: true });
  try {
    for (const headers of [{}, { Origin: "https://evil.example" }, { Origin: origin }]) {
      const response = await context.request.post(`${proxyOrigin}/_blazor/negotiate?negotiateVersion=1`, {
        headers: { ...headers, Host: new URL(externalOrigin).host },
      });
      assert.equal(response.status(), 403);
    }
    const upgrade = await context.request.get(`${proxyOrigin}/_blazor`, {
      headers: {
        Host: new URL(externalOrigin).host,
        Connection: "Upgrade",
        Upgrade: "websocket",
        "Sec-WebSocket-Version": "13",
        "Sec-WebSocket-Key": "dGhlIHNhbXBsZSBub25jZQ==",
      },
    });
    assert.equal(upgrade.status(), 403);
    const localSpoof = await context.request.get(`${origin}/join`, { headers: { Host: new URL(externalOrigin).host, "X-Forwarded-Proto": "https" } });
    assert.equal(localSpoof.status(), 400);
  } finally {
    await context.close();
  }
});

test("rejected invitation authentication keeps invalid-invitation presentation", async () => {
  const context = await browser.newContext({ ignoreHTTPSErrors: true });
  try {
    const page = await context.newPage();
    await page.goto(`${await getPublicOrigin()}/join#invite=rejected-invite`);
    await page.getByTestId("guest-invalid").waitFor({ state: "visible" });
    assert.equal(new URL(page.url()).hash, "");
    assert.equal(await page.getByTestId("guest-disconnected").isVisible(), false);
  } finally {
    await context.close();
  }
});

test("public browser circuit loss closes its API connection and disables control", async () => {
  const externalOrigin = await getPublicOrigin();
  const invitation = "public-circuit-loss";
  const session = { state: guestState(), closed: Promise.withResolvers() };
  sessions.set(invitation, session);
  const context = await browser.newContext({ ignoreHTTPSErrors: true });
  const deadline = setTimeout(() => session.closed.reject(new Error("Guest circuit did not close its API connection")), 10_000);
  try {
    const { page } = await openGuest(context, invitation, true, externalOrigin);
    for (const socket of proxySockets) socket.destroy();
    await session.closed.promise;
    await page.getByTestId("guest-disconnected").waitFor({ state: "visible" });
    assert.equal(await page.locator("[data-send-button]").isDisabled(), true);
  } finally {
    clearTimeout(deadline);
    await context.close();
    sessions.delete(invitation);
  }
});

test("local AdminWeb builds public invitation links and rejects public requests", async () => {
  const externalOrigin = await getPublicOrigin();
  const reservation = createServer();
  const port = await listen(reservation);
  await new Promise((resolve) => reservation.close(resolve));
  await startHost(adminProject, adminArtifact, {
    OSSM__Admin__AdminPort: String(port),
    OSSM__Admin__GuestHostname: externalOrigin,
  });
  const context = await browser.newContext();
  try {
    const page = await context.newPage();
    await page.goto(`http://127.0.0.1:${port}`);
    const link = page.getByTestId("invitation-link");
    await link.waitFor({ state: "visible" });
    assert.equal(await link.inputValue(), `${externalOrigin}/join#invite=isolated-admin-invite`);
    const response = await context.request.get(`http://127.0.0.1:${port}`, {
      headers: { Host: new URL(externalOrigin).host, "X-Forwarded-Proto": "https", Origin: externalOrigin },
    });
    assert.equal(response.status(), 400);
  } finally {
    await context.close();
  }
});

async function requestGuestStop(page, session) {
  const received = Promise.withResolvers();
  session.stopReceived = received.resolve;
  const deadline = setTimeout(() => received.reject(new Error("Guest Stop did not reach the fake peer")), 5_000);
  try {
    await page.locator("[data-stop-button]").click();
    await received.promise;
  } finally {
    clearTimeout(deadline);
  }
}

for (const [pendingCommand, method, label] of [
  ["Connect", "Stop", "Stop"],
  ["Connect", "EndGuest", "End guest"],
  ["AllowGuest", "Stop", "Stop"],
  ["AllowGuest", "EndGuest", "End guest"],
]) {
  test(
    `admin ${label} bypasses pending ${pendingCommand === "Connect" ? "Connect" : "Allow"} and bounds duplicate safety commands`,
    { timeout: 25_000 },
    async () => {
      const adminOrigin = await getAdminOrigin();
      const context = await browser.newContext();
      adminSession = newAdminSession();
      const waitForCommand = async (name) => {
        const received = adminSession.received.get(name);
        const deadline = setTimeout(
          () => received.reject(new Error(`${name} did not reach the fake peer while ${pendingCommand} was pending`)),
          5_000,
        );
        try {
          await received.promise;
        } finally {
          clearTimeout(deadline);
        }
      };
      try {
        const page = await context.newPage();
        page.setDefaultTimeout(5_000);
        await page.goto(adminOrigin);
        await page.getByTestId("user-controls").waitFor({ state: "visible" });
        const connect = page.getByRole("button", { name: pendingCommand === "Connect" ? "Connect device" : "Allow control", exact: true });
        const stop = page.getByRole("button", { name: "Stop", exact: true });
        const endGuest = page.getByRole("button", { name: "End guest", exact: true });
        await connect.click();
        await waitForCommand(pendingCommand);
        await page.waitForFunction(
          (name) => [...document.querySelectorAll("button")].some((button) => button.textContent.trim() === name && button.disabled),
          pendingCommand === "Connect" ? "Connect device" : "Allow control",
        );
        assert.equal(await stop.isEnabled(), true, "Stop must stay enabled while the normal command awaits its response");
        assert.equal(await endGuest.isEnabled(), true, "End guest must stay enabled while the normal command awaits its response");
        assert.deepEqual(
          await page.locator(".preview-rail").getAttribute("aria-label"),
          "Unknown preview at Unknown speed, Unknown stroke, Unknown depth, and Unknown sensation",
          "pending commands must not invent sent settings",
        );
        const button = page.getByRole("button", { name: label, exact: true });
        await button.click();
        await waitForCommand(method);
        await page.waitForFunction(
          (name) =>
            [...document.querySelectorAll("button")].some((element) => element.textContent.trim().replace(/\s+/g, " ") === name && element.disabled),
          label,
        );
        await button.evaluate((element) => {
          element.click();
          element.click();
        });
        adminSession.pending.get(method)();
        adminSession.pending.delete(method);
        await page.waitForFunction(
          (name) =>
            [...document.querySelectorAll("button")].some((element) => element.textContent.trim().replace(/\s+/g, " ") === name && !element.disabled),
          label,
        );
        assert.equal(await connect.isDisabled(), true, "Safety completion must not clear the pending normal command state");
        assert.equal(await page.getByRole("button", { name: "Reset", exact: true }).count(), 0);
        assert.deepEqual(adminSession.calls, [pendingCommand, method]);
        adminSession.pending.get(pendingCommand)();
        adminSession.pending.delete(pendingCommand);
        await page.waitForFunction(
          (name) => [...document.querySelectorAll("button")].some((button) => button.textContent.trim() === name && !button.disabled),
          pendingCommand === "Connect" ? "Connect device" : "Allow control",
        );
        if (pendingCommand === "Connect") {
          await page.getByRole("alert").filter({ hasText: "Device connection is not confirmed." }).waitFor();
        }
      } finally {
        for (const reply of adminSession.pending.values()) reply();
        await context.close();
        adminSession = null;
      }
    },
  );
}

async function openAdmin(state) {
  adminSession = newAdminSession(state);
  const context = await browser.newContext();
  const page = await context.newPage();
  page.setDefaultTimeout(7_000);
  const errors = [];
  const frames = [];
  page.on("pageerror", (error) => errors.push(error.message));
  page.on("console", (message) => {
    if (message.type() === "error") errors.push(message.text());
  });
  page.on("websocket", (socket) => socket.on("framereceived", ({ payload }) => frames.push(payload)));
  await page.goto(await getAdminOrigin());
  await page.getByTestId("device-connection").waitFor({ state: "visible" });
  return { context, page, errors, frames };
}

async function closeAdmin(context) {
  for (const reply of adminSession.pending.values()) reply();
  await context.close();
  adminSession = null;
}

async function selectSpeed(page, value) {
  await page.locator('[data-input="speed"]').evaluate((input, value) => {
    input.value = String(value);
    input.dispatchEvent(new Event("input", { bubbles: true }));
  }, value);
}

async function filtersOnAncestors(locator) {
  return locator.evaluate((element) => {
    const filters = [];
    for (let current = element; current; current = current.parentElement) {
      const filter = getComputedStyle(current).filter;
      if (filter !== "none") filters.push(filter);
    }
    return filters;
  });
}

test("admin connection widget distinguishes unknown, disconnected, connecting and failed states", async () => {
  const { context, page, errors } = await openAdmin(userState());
  try {
    const status = page.getByTestId("device-connection-status");
    assert.equal(await status.innerText(), "Unknown");
    assert.equal(await page.getByTestId("device-name").innerText(), "Unknown");
    assert.equal(await page.getByTestId("device-identifier").innerText(), "Unknown");
    assert.equal(await page.getByTestId("device-duration").innerText(), "Unknown");
    assert.equal(await page.getByRole("button", { name: "Reset", exact: true }).count(), 0);
    assert.equal(await page.getByRole("button", { name: "Allow control", exact: true }).count(), 1);
    assert.doesNotMatch(await page.locator("main").innerText(), /Permission expires|Not active|Last known device status|Allow guest/);
    assert.equal(await page.locator("rw-card rw-card").count(), 0, "widgets must not nest cards");
    for (const [expected, overrides] of [
      ["Unknown", null],
      ["Disconnected", { isArmed: true }],
      ["Connecting", { isConnecting: true, connectionFailed: true }],
      ["Failed", { connectionFailed: true }],
      ["Connected", { isConnected: true, isArmed: false }],
    ]) {
      adminSession.state = userState(overrides === null ? null : deviceState(overrides));
      await page.waitForFunction(
        (expected) => document.querySelector('[data-testid="device-connection-status"]')?.textContent === expected,
        expected,
      );
      assert.equal(await status.innerText(), expected);
      const connected = overrides?.isConnected === true && !overrides.isConnecting && !overrides.connectionFailed;
      await page.waitForFunction(
        (connected) => document.querySelector(".user-grid").hasAttribute("data-device-disconnected") !== connected,
        connected,
      );
      assert.deepEqual(
        await filtersOnAncestors(page.getByTestId("device-connection")),
        [],
        "device recovery must remain coloured, including its ancestors",
      );
      for (const selector of ['[data-testid="user-controls"]', '[data-testid="guest-invitation-card"]', ".pattern-preview"]) {
        assert.deepEqual(await filtersOnAncestors(page.locator(selector)), connected ? [] : ["grayscale(1)"], `${expected}: ${selector}`);
      }
      assert.equal(await page.getByTestId("device-duration").innerText(), "Unknown", "missing connection completion time must not be invented");
      if (expected === "Failed") {
        assert.equal(await page.getByRole("button", { name: "Connect device", exact: true }).isEnabled(), true);
        for (const viewport of viewports) {
          await page.setViewportSize(viewport);
          assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
          if (process.env.GUEST_SCREENSHOT_DIR) {
            await page.screenshot({ path: join(process.env.GUEST_SCREENSHOT_DIR, `ossm-admin-device-loss-${viewport.width}.png`), fullPage: true });
          }
        }
      }
    }
    adminSession.state = userState(deviceState());
    await page.waitForFunction(() => document.querySelector('[data-testid="device-connection-status"]')?.textContent === "Disconnected");
    await page.getByRole("button", { name: "Connect device", exact: true }).focus();
    await page.keyboard.press("Enter");
    await adminSession.received.get("Connect").promise;
    adminSession.pending.get("Connect")({ accepted: false, code: "connection-failed" });
    adminSession.pending.delete("Connect");
    await page.getByRole("alert").filter({ hasText: "The action was rejected (connection-failed)." }).waitFor();
    assert.equal(await page.getByText("Device connected.", { exact: true }).count(), 0);
    assert.deepEqual(errors, []);
  } finally {
    await closeAdmin(context);
  }
});

test("admin completed connection shows metadata and a JS duration without server renders", async () => {
  const { context, page, errors, frames } = await openAdmin(userState(deviceState()));
  try {
    assert.equal(await page.getByText("Connecting...", { exact: true }).count(), 0, "connection state belongs in the device pill");
    await page.evaluate(() => {
      window.adminIntervals = new Set();
      const set = window.setInterval;
      const clear = window.clearInterval;
      window.setInterval = (...args) => {
        const interval = set(...args);
        if (new Error().stack.includes("/admin.js")) window.adminIntervals.add(interval);
        return interval;
      };
      window.clearInterval = (interval) => {
        window.adminIntervals.delete(interval);
        clear(interval);
      };
    });
    await page.getByRole("button", { name: "Connect device", exact: true }).click();
    await adminSession.received.get("Connect").promise;
    adminSession.state = userState(deviceState({ isConnecting: true }));
    await page.waitForFunction(() => document.querySelector('[data-testid="device-connection-status"]')?.textContent === "Connecting");
    assert.equal(await page.getByText("Connecting...", { exact: true }).count(), 0, "connecting must not add a separate notice");
    assert.equal(await page.getByText("Device connected.", { exact: true }).count(), 0);
    assert.equal(await page.getByText("Device connection requested.", { exact: true }).count(), 0);
    assert.equal(await page.evaluate(() => window.adminIntervals.size), 0);
    const connectedAt = new Date((await page.evaluate(() => Date.now())) - 3_661_000).toISOString();
    adminSession.state = userState(
      deviceState({
        isConnected: true,
        isArmed: false,
        connectedAt,
        name: "Test device with a deliberately long descriptive name",
        identifier: "isolated-device-" + "1234567890".repeat(10),
        transmittedSettings: { pattern: 4, speed: 27, stroke: 38, depth: 49, sensation: 61 },
      }),
    );
    adminSession.pending.get("Connect")();
    adminSession.pending.delete("Connect");
    await page.waitForFunction(() => document.querySelector('[data-testid="device-connection-status"]')?.textContent === "Connected");
    assert.equal(await page.getByText("Connecting...", { exact: true }).count(), 0, "connecting must not remain after connection");
    assert.equal(await page.getByText("Device connected.", { exact: true }).count(), 0, "the connection pill must not be duplicated by a notice");
    assert.equal(await page.getByTestId("device-connection-status").innerText(), "Connected");
    assert.equal(await page.getByTestId("device-name").innerText(), adminSession.state.device.name);
    assert.equal(await page.getByTestId("device-identifier").innerText(), adminSession.state.device.identifier);
    await page.waitForFunction(() => /^01:01:\d{2}$/.test(document.querySelector('[data-testid="device-duration"]')?.textContent));
    assert.equal(await page.locator(".preview-carriage animate").count(), 1, "connectivity, not arming, permits the preview animation");
    const duration = page.getByTestId("device-duration");
    const before = await duration.innerText();
    const frameCount = frames.length;
    await page.waitForFunction((before) => {
      const seconds = (value) => value.split(":").reduce((total, part) => total * 60 + Number(part), 0);
      return seconds(document.querySelector('[data-testid="device-duration"]').textContent) >= seconds(before) + 2;
    }, before);
    const after = await duration.innerText();
    const seconds = (value) => value.split(":").reduce((total, part) => total * 60 + Number(part), 0);
    assert.ok(seconds(after) >= seconds(before) + 2, "duration must advance without a new device snapshot");
    assert.equal(frames.length, frameCount, "the browser timer must not need Blazor render frames");
    assert.equal(Date.parse(await duration.getAttribute("data-connected-at")), Date.parse(connectedAt));
    assert.equal(adminSession.state.device.connectedAt, connectedAt);
    assert.equal(await page.evaluate(() => window.adminIntervals.size), 1);
    for (const viewport of viewports) {
      await page.setViewportSize(viewport);
      assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
      assert.equal(await page.getByRole("button", { name: "Stop", exact: true }).isVisible(), true);
      assert.equal(await page.getByRole("button", { name: "End guest", exact: true }).isVisible(), true);
      if (process.env.GUEST_SCREENSHOT_DIR) {
        await page.screenshot({ path: join(process.env.GUEST_SCREENSHOT_DIR, `ossm-admin-connected-${viewport.width}.png`), fullPage: true });
      }
    }
    adminSession.state = userState({ ...adminSession.state.device, isConnecting: true });
    await page.waitForFunction(() => document.querySelector('[data-testid="device-connection-status"]')?.textContent === "Connecting");
    assert.equal(await duration.innerText(), "Unknown");
    assert.equal(await page.evaluate(() => window.adminIntervals.size), 0, "a disconnecting/reconnecting device must release the old interval");
    assert.equal(await page.locator(".preview-carriage animate").count(), 0);
    adminSession.state = userState({ ...adminSession.state.device, isConnected: false, isConnecting: false, connectedAt: null });
    await page.waitForFunction(() => document.querySelector('[data-testid="device-connection-status"]')?.textContent === "Disconnected");
    assert.equal(await duration.innerText(), "Unknown");
    assert.equal(await page.evaluate(() => window.adminIntervals.size), 0);
    assert.equal(await page.locator(".preview-carriage animate").count(), 0);
    assert.equal(
      await page.locator(".preview-rail").getAttribute("aria-label"),
      "Pattern 4 preview at 27% speed, 38% stroke, 49% depth, and 61% sensation",
    );
    adminSession.state = userState({ ...adminSession.state.device, isConnected: true, connectedAt });
    await page.waitForFunction(() => document.querySelector('[data-testid="device-duration"]')?.dataset.connectedAt);
    assert.equal(await page.evaluate(() => window.adminIntervals.size), 1);
    assert.equal(await page.locator(".preview-carriage animate").count(), 1);
    assert.deepEqual(await filtersOnAncestors(page.locator(".pattern-preview")), []);
    assert.equal(await page.getByText("Device connected.", { exact: true }).count(), 0);
    await duration.evaluate((element) => element.remove());
    await page.waitForFunction(() => window.adminIntervals.size === 0);
    assert.equal(await page.evaluate(() => window.adminIntervals.size), 0, "removing the timer element must release its interval");
    assert.deepEqual(errors, []);
  } catch (error) {
    assert.fail(
      `${error.message}\nRendered UI: ${await page.locator("main").innerText()}\nPeer reads: ${adminSession.reads}\nBrowser errors: ${JSON.stringify(errors)}`,
    );
  } finally {
    await closeAdmin(context);
  }
});

test("admin sent settings never substitute observed values or defaults for partial transmissions", async () => {
  const { context, page, errors } = await openAdmin(userState(deviceState({ isConnected: true })));
  try {
    const preview = page.locator(".preview-rail");
    assert.equal(await preview.getAttribute("aria-label"), "Unknown preview at Unknown speed, Unknown stroke, Unknown depth, and Unknown sensation");
    assert.equal(await page.getByTestId("preview-pattern").innerText(), "Unknown");
    assert.equal(await page.locator(".preview-range, .preview-carriage").count(), 0);
    assert.equal(await page.getByRole("heading", { name: "Last settings", exact: true }).count(), 1);
    for (const [settings, expected, range, animation] of [
      [{ pattern: null, speed: 17, stroke: null, depth: null, sensation: null }, ["17%", "Unknown", "Unknown", "Unknown"], 0, 0],
      [{ pattern: null, speed: 17, stroke: null, depth: 47, sensation: null }, ["17%", "Unknown", "47%", "Unknown"], 0, 0],
      [{ pattern: null, speed: 17, stroke: 36, depth: null, sensation: null }, ["17%", "36%", "Unknown", "Unknown"], 0, 0],
      [{ pattern: 0, speed: null, stroke: 36, depth: 47, sensation: null }, ["Unknown", "36%", "47%", "Unknown"], 1, 0],
      [{ pattern: 0, speed: 0, stroke: 36, depth: 47, sensation: 59 }, ["0%", "36%", "47%", "59%"], 1, 0],
      [{ pattern: 5, speed: 28, stroke: 36, depth: 47, sensation: 59 }, ["28%", "36%", "47%", "59%"], 1, 1],
    ]) {
      adminSession.state = userState({ ...adminSession.state.device, transmittedSettings: settings });
      const patternLabel = settings.pattern === null ? "Unknown" : settings.pattern === 0 ? "Simple stroke" : "Pattern 5";
      const label = `${patternLabel} preview at ${expected[0]} speed, ${expected[1]} stroke, ${expected[2]} depth, and ${expected[3]} sensation`;
      await page.waitForFunction((label) => document.querySelector(".preview-rail")?.getAttribute("aria-label") === label, label);
      assert.equal(await preview.getAttribute("aria-label"), label);
      assert.equal(await page.locator(".preview-range").count(), range);
      assert.equal(await page.locator(".preview-carriage animate").count(), animation);
      assert.equal(
        await page.getByTestId("preview-pattern").innerText(),
        settings.pattern === null ? "Unknown" : settings.pattern === 0 ? "Simple stroke" : "Pattern 5",
      );
    }
    assert.deepEqual(adminSession.calls, [], "displaying transmissions must not issue commands");
    assert.deepEqual(errors, []);
  } finally {
    await closeAdmin(context);
  }
});

test("user and guest previews omit stats and user names the last settings", async () => {
  const admin = await openAdmin(userState(deviceState({ isConnected: true })));
  try {
    assert.equal(await admin.page.locator(".preview-settings").count(), 0);
    assert.equal(await admin.page.getByRole("heading", { name: "Last settings", exact: true }).count(), 1);
    assert.deepEqual(admin.errors, []);
  } finally {
    await closeAdmin(admin.context);
  }
  const context = await browser.newContext();
  try {
    const guest = await openGuest(context, "preview-without-stats");
    assert.equal(await guest.page.locator(".preview-settings").count(), 0);
    assert.equal(await guest.page.locator(".preview-rail").count(), 1);
    assert.deepEqual(guest.errors, []);
  } finally {
    await context.close();
  }
});

test("admin preview derives timing only from transmitted velocity and stroke", async () => {
  const { context, page, errors } = await openAdmin(userState(deviceState({ isConnected: true })));
  try {
    for (const [speed, stroke, depth, duration] of [
      [5, 10, 10, "0.8s"],
      [40, 80, 100, "0.8s"],
      [5, 10, 80, "0.8s"],
      [50, 10, 80, "0.08s"],
      [0, 10, 80, null],
      [5, 0, 80, null],
      [5, 10, 0, null],
    ]) {
      adminSession.state = userState({ ...adminSession.state.device, transmittedSettings: { pattern: 0, speed, stroke, depth, sensation: 50 } });
      const label = `Simple stroke preview at ${speed}% speed, ${stroke}% stroke, ${depth}% depth, and 50% sensation`;
      await page.waitForFunction((label) => document.querySelector(".preview-rail")?.getAttribute("aria-label") === label, label);
      const animation = page.locator(".preview-carriage animate");
      assert.equal(await animation.count(), duration === null ? 0 : 1);
      if (duration !== null) assert.equal(await animation.getAttribute("dur"), duration);
    }
    adminSession.state = userState({
      ...adminSession.state.device,
      transmittedSettings: { pattern: 0, speed: 5, stroke: 10, depth: 20, sensation: 50 },
    });
    await page.waitForFunction(() => document.querySelector(".preview-carriage animate")?.getAttribute("dur") === "0.8s");
    for (const viewport of viewports) {
      await page.setViewportSize(viewport);
      const positions = await page.locator(".preview-rail").evaluate((rail) => {
        rail.pauseAnimations();
        rail.setCurrentTime(0.1);
        const first = rail.querySelector(".preview-carriage").cx.animVal.value;
        rail.setCurrentTime(0.3);
        const second = rail.querySelector(".preview-carriage").cx.animVal.value;
        rail.unpauseAnimations();
        return [first, second];
      });
      assert.ok(Math.abs(positions[1] - positions[0]) > 1, "the carriage must visibly move within its transmitted range");
      assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
      if (process.env.GUEST_SCREENSHOT_DIR) {
        await page.screenshot({ path: join(process.env.GUEST_SCREENSHOT_DIR, `ossm-speed-preview-${viewport.width}.png`), fullPage: true });
      }
    }
    assert.deepEqual(adminSession.calls, [], "preview timing must not send commands");
    assert.deepEqual(errors, []);
  } finally {
    await closeAdmin(context);
  }
});

test("guest speed scales with stroke length without multiplying by depth", async () => {
  const context = await browser.newContext();
  const invitation = "stroke-relative-speed";
  const session = { renewals: 0, submissions: [], state: guestState() };
  sessions.set(invitation, session);
  try {
    const { page, errors } = await openGuest(context, invitation);
    for (const [speed, stroke, depth, expectedSpeed] of [
      [50, 10, 10, 5],
      [50, 80, 100, 40],
      [50, 10, 80, 5],
      [25, 10, 10, 2],
      [100, 100, 100, 100],
      [0, 10, 20, 0],
      [30, 1, 1, 0],
      [100, 1, 1, 1],
    ]) {
      await page.evaluate(
        ({ speed, stroke, depth }) => {
          for (const [name, value] of Object.entries({ depth, stroke, speed })) {
            const input = document.querySelector(`[data-input="${name}"]`);
            input.value = String(value);
            input.dispatchEvent(new Event("input", { bubbles: true }));
          }
        },
        { speed, stroke, depth },
      );
      const count = session.submissions.length;
      await page.locator("[data-send-button]").click();
      await page.waitForFunction(
        ({ speed, stroke, depth }) =>
          document.querySelector('[data-sent="speed"]')?.textContent === String(speed) &&
          document.querySelector('[data-sent="stroke"]')?.textContent === String(stroke) &&
          document.querySelector('[data-sent="depth"]')?.textContent === String(depth),
        { speed, stroke, depth },
      );
      assert.equal(session.submissions.length, count + 1);
      const submitted = session.submissions.at(-1);
      assert.deepEqual([submitted.speed, submitted.stroke, submitted.depth], [expectedSpeed, stroke, depth]);
      const currentSpeed = Math.round((expectedSpeed * 100) / stroke);
      await page.waitForFunction(
        (currentSpeed) => document.querySelector('[data-current="speed"]')?.textContent === String(currentSpeed),
        currentSpeed,
      );
      assert.equal(await page.locator('[data-sent-marker="speed"]').getAttribute("y1"), String(100 - speed));
      assert.equal(await page.locator('[data-current-marker="speed"]').getAttribute("y1"), String(100 - currentSpeed));
      assert.match(await page.locator('[data-input="speed"]').getAttribute("aria-valuetext"), /Current value:/);
      assert.doesNotMatch(await page.locator("main").innerText(), /Last known/);
      assert.equal(await page.locator('[data-input="speed"]').inputValue(), String(speed), "the selected cadence must remain unchanged");
      const animation = page.locator(".preview-carriage animate");
      assert.equal(await animation.count(), expectedSpeed > 0 ? 1 : 0);
      if (expectedSpeed > 0) {
        assert.equal(await animation.getAttribute("dur"), `${Number(((0.4 * stroke) / expectedSpeed).toFixed(4))}s`);
      }
      await page.locator("[data-send-button]").click();
      assert.equal(session.submissions.length, count + 1, "unchanged selection must not resend its converted command");
    }
    for (const [speed, stroke, expected] of [
      [5, 10, 50],
      [2.5, 20, 13],
      [null, 10, null],
      [5, null, null],
      [0, 0, null],
      [0, 10, 0],
      [40, 10, 100],
    ]) {
      session.state = guestState({ device: { ...session.state.device, speed, stroke } });
      await page.waitForFunction((expected) => document.querySelector('[data-current="speed"]')?.textContent === String(expected ?? "--"), expected);
      assert.equal(await page.locator('[data-current-marker="speed"]').getAttribute("hidden"), expected === null ? "" : null);
    }
    session.state = guestState({ device: { ...session.state.device, speed: 5, stroke: 10 } });
    await page.waitForFunction(() => document.querySelector('[data-current="speed"]')?.textContent === "50");
    await page.locator('[data-input="stroke"]').evaluate((input) => {
      input.value = "1";
      input.dispatchEvent(new Event("input", { bubbles: true }));
    });
    await selectSpeed(page, 50);
    assert.equal(await page.locator('[data-current="speed"]').innerText(), "50", "unsent edits must not change the device speed marker");
    for (const viewport of viewports) {
      await page.setViewportSize(viewport);
      assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
      if (process.env.GUEST_SCREENSHOT_DIR) {
        await page.screenshot({ path: join(process.env.GUEST_SCREENSHOT_DIR, `ossm-guest-current-speed-${viewport.width}.png`), fullPage: true });
      }
    }
    assert.deepEqual(errors, []);
  } finally {
    await context.close();
    sessions.delete(invitation);
  }
});

test("auto-send label fits beside its switch at desktop and mobile widths", async () => {
  const context = await browser.newContext();
  await context.route(/\/guest(?:\.[^/]+)?\.css(?:\?.*)?$/, async (route) => {
    await route.fulfill({ contentType: "text/css", body: await readFile(join(project, "wwwroot/guest.css"), "utf8") });
  });
  const invitation = "auto-send-layout";
  sessions.set(invitation, { state: guestState() });
  try {
    const { page, errors } = await openGuest(context, invitation);
    for (const viewport of viewports) {
      await page.setViewportSize(viewport);
      const layout = await page.locator(".auto-send").evaluate((toggle) => {
        const label = toggle.querySelector("label");
        const range = document.createRange();
        range.selectNodeContents(label);
        const textBounds = range.getBoundingClientRect();
        const labelBounds = label.getBoundingClientRect();
        const toggleBounds = toggle.getBoundingClientRect();
        return {
          labelWidth: labelBounds.width,
          textWidth: textBounds.width,
          rightSpace: toggleBounds.right - textBounds.right,
          toggleWidth: toggleBounds.width,
        };
      });
      assert.ok(layout.labelWidth >= layout.textWidth, JSON.stringify(layout));
      assert.ok(layout.rightSpace >= 48, "the label must leave room for the switch and its gap: " + JSON.stringify(layout));
      assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
      if (process.env.GUEST_SCREENSHOT_DIR) {
        await page.screenshot({ path: join(process.env.GUEST_SCREENSHOT_DIR, `ossm-guest-auto-send-${viewport.width}.png`), fullPage: true });
      }
    }
    assert.deepEqual(errors, []);
  } finally {
    await context.close();
    sessions.delete(invitation);
  }
});

test("deadline-free permission recovers after revoke, Allow and rearm without automatic motion", async () => {
  const context = await browser.newContext();
  const invitation = "permission-regrant";
  const session = { renewals: 0, submissions: [], state: guestState() };
  sessions.set(invitation, session);
  try {
    const { page, errors } = await openGuest(context, invitation);
    assert.equal(await page.locator(".guest-session-message").innerText(), "Control enabled.");
    assert.deepEqual(await filtersOnAncestors(page.locator(".guest-interface")), []);
    await selectSpeed(page, 30);
    await page.locator("[data-auto-send]").check();
    await page.waitForFunction(() => document.querySelector('[data-sent="speed"]')?.textContent === "30");
    assert.equal(session.submissions.length, 1);
    session.state = guestState({ controlAllowed: false });
    await page.waitForFunction(
      () =>
        document.querySelector(".guest-session-message")?.textContent.includes("Waiting for the user") ||
        document.documentElement.dataset.guestConnectionLost === "true",
    );
    assert.match(await page.locator("main").innerText(), /Waiting for the user to allow control\./);
    for (const viewport of viewports) {
      await page.setViewportSize(viewport);
      assert.deepEqual(await filtersOnAncestors(page.locator(".guest-interface")), ["grayscale(1)"]);
      assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
      if (process.env.GUEST_SCREENSHOT_DIR) {
        await page.screenshot({ path: join(process.env.GUEST_SCREENSHOT_DIR, `ossm-guest-waiting-${viewport.width}.png`), fullPage: true });
      }
    }
    assert.equal(await page.locator("[data-send-button]").isDisabled(), true);
    assert.equal(await page.locator("[data-auto-send]").isChecked(), false);
    session.state = guestState({ device: { ...session.state.device, isArmed: false } });
    await page.waitForFunction(() => document.querySelector(".guest-session-message")?.textContent.includes("Control enabled."));
    assert.equal(await page.locator(".guest-session-message").innerText(), "Control enabled.");
    assert.deepEqual(await filtersOnAncestors(page.locator(".guest-interface")), []);
    assert.equal(await page.locator("[data-send-button]").isDisabled(), true, "Allow alone must not bypass a disarmed device");
    session.state.device.isArmed = true;
    await page.waitForFunction(() => document.querySelector("[data-send-button]")?.disabled === false);
    assert.equal(session.submissions.length, 1, "regrant and rearm must not submit the saved selection");
    assert.equal(await page.locator("[data-auto-send]").isChecked(), false);
    await page.locator("[data-send-button]").click();
    await page.waitForFunction(() => document.querySelector('[data-sent="speed"]')?.textContent === "30");
    assert.equal(session.submissions.length, 2, "the same selection must be deliberately sendable after regrant");
    assert.equal(session.renewals, 0);
    assert.equal(await page.getByTestId("guest-disconnected").isVisible(), false);
    assert.equal(await page.evaluate(() => sessionStorage.getItem("ossm-guest-connection-lost")), null);
    for (const viewport of viewports) {
      await page.setViewportSize(viewport);
      assert.equal(await page.locator(".guest-session-message").isVisible(), true);
      assert.equal(await page.locator("[data-send-button]").isVisible(), true);
      assert.equal(await page.locator("[data-stop-button]").isVisible(), true);
      assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
      if (process.env.GUEST_SCREENSHOT_DIR) {
        await page.screenshot({ path: join(process.env.GUEST_SCREENSHOT_DIR, `ossm-guest-permission-${viewport.width}.png`), fullPage: true });
      }
    }
    assert.deepEqual(errors, [], "the recovery flow must not produce browser errors");
  } finally {
    await context.close();
    sessions.delete(invitation);
  }
});

test("guest Stop preserves permission and only acknowledged Stop permits deliberate resumption", async () => {
  const context = await browser.newContext();
  const invitation = "stop-resume";
  const session = { renewals: 0, stops: 0, submissions: [], holdStop: true, state: guestState() };
  sessions.set(invitation, session);
  try {
    const { page, errors } = await openGuest(context, invitation);
    await selectSpeed(page, 30);
    await page.locator("[data-auto-send]").check();
    await page.waitForFunction(() => document.querySelector('[data-sent="speed"]')?.textContent === "30");
    await page.waitForFunction(() => document.querySelector('[data-current="speed"]')?.textContent === "30");
    await requestGuestStop(page, session);
    await page.waitForFunction(() => document.querySelector("[data-send-button]")?.disabled === true);
    assert.equal(session.stops, 1);
    assert.equal(await page.locator("[data-auto-send]").isChecked(), false);
    session.completeStop({ accepted: false, code: "outcome-unknown" });
    await page.getByText(/Stop was not acknowledged/).waitFor({ state: "visible" });
    assert.equal(await page.locator("[data-send-button]").isDisabled(), true);
    await requestGuestStop(page, session);
    session.completeStop();
    await page.waitForFunction(() => document.querySelector('[data-current="speed"]')?.textContent === "0");
    await page.waitForFunction(() => document.querySelector("[data-send-button]")?.disabled === false);
    assert.equal(session.state.controlAllowed, true);
    assert.equal(session.state.device.isArmed, false);
    assert.equal(session.state.canResumeAfterStop, true);
    assert.equal(session.submissions.length, 1, "Stop acknowledgement must not automatically resend");
    await page.locator("[data-send-button]").click();
    await page.waitForFunction(() => document.querySelector('[data-sent="speed"]')?.textContent === "30");
    assert.equal(session.submissions.length, 2);
    assert.equal(session.stops, 2);
    assert.equal(session.renewals, 0);
    assert.deepEqual(errors, [], "the Stop recovery flow must not produce browser errors");
  } finally {
    await context.close();
    sessions.delete(invitation);
  }
});

for (const [name, overrides] of [
  ["missing device", { device: null, canResumeAfterStop: true }],
  ["disarmed device without guest Stop", { device: { ...guestState().device, isArmed: false } }],
  ["failed armed device", { device: { ...guestState().device, connectionFailed: true } }],
  ["failed stopped device", { device: { ...guestState().device, isArmed: false, connectionFailed: true }, canResumeAfterStop: true }],
]) {
  test(`permission alone cannot send with ${name}`, async () => {
    const context = await browser.newContext();
    const invitation = `readiness-${name.replaceAll(" ", "-")}`;
    const session = { renewals: 0, submissions: [], state: guestState(overrides) };
    sessions.set(invitation, session);
    try {
      const { page } = await openGuest(context, invitation, false);
      assert.equal(await page.locator("[data-send-button]").isDisabled(), true);
      assert.equal(await page.locator("[data-auto-send]").isDisabled(), true);
      await page.locator("[data-send-button]").evaluate((button) => button.click());
      session.state = guestState();
      delete session.state.canResumeAfterStop;
      await page.waitForFunction(() => document.querySelector("[data-send-button]")?.disabled === false);
      assert.equal(session.submissions.length, 0, "device recovery must not send a selection automatically");
    } finally {
      await context.close();
      sessions.delete(invitation);
    }
  });
}

test("polling a guest that is no longer joined ends the session without transport failure", async () => {
  const context = await browser.newContext();
  const invitation = "session-ended";
  const session = { renewals: 0, state: guestState() };
  sessions.set(invitation, session);
  try {
    const { page } = await openGuest(context, invitation);
    session.state = guestState({ joined: false, controlAllowed: false, device: null });
    await page.getByTestId("guest-ended").waitFor({ state: "visible" });
    assert.equal(await page.locator("[data-send-button]:not([disabled])").count(), 0);
    assert.equal(session.renewals, 0);
    assert.equal(await page.evaluate(() => sessionStorage.getItem("ossm-guest-connection-lost")), null);
  } finally {
    await context.close();
    sessions.delete(invitation);
  }
});

for (const code of ["unexpected-rejection", "connection-unavailable", "outcome-unknown"]) {
  test(`polling failure ${code} still disconnects persistently`, async () => {
    const context = await browser.newContext();
    const invitation = `polling-${code}`;
    const session = {
      renewals: 0,
      state: guestState(),
    };
    sessions.set(invitation, session);
    try {
      const { page } = await openGuest(context, invitation);
      await selectSpeed(page, 50);
      await page.locator(".preview-carriage animate").waitFor({ state: "attached" });
      const settings = await page.locator(".preview-rail").getAttribute("aria-label");
      session.pollFailure = code;
      await page.getByTestId("guest-disconnected").waitFor({ state: "visible" });
      assert.equal(await page.locator(".guest-session").count(), 1, "service loss must retain the actual joined view");
      assert.equal(await page.locator(".preview-rail").getAttribute("aria-label"), settings);
      await page.locator(".preview-carriage animate").waitFor({ state: "detached" });
      assert.equal(await page.locator('[data-input="speed"]').inputValue(), "50");
      assert.equal(session.renewals, 0);
      assert.equal(await page.evaluate(() => sessionStorage.getItem("ossm-guest-connection-lost")), "true");
      assert.equal(await page.locator("[data-send-button]:not([disabled])").count(), 0);
      await page.reload();
      await page.getByTestId("guest-disconnected").waitFor({ state: "visible" });
    } finally {
      await context.close();
      sessions.delete(invitation);
    }
  });
}

test("reopened invite is redeemed after a fresh document without a capability navigation URI", async () => {
  const context = await browser.newContext();
  try {
    const { page, frames } = await openGuest(context, "first-test-invite");
    assert.ok(invitations.includes("first-test-invite"));
    await page.goto(`${origin}/join#invite=reopened-test-invite`);
    await page.waitForFunction(() => !location.hash && document.querySelector("[data-send-button]")?.disabled === false);
    assert.ok(invitations.includes("reopened-test-invite"));
    assert.equal(
      frames.some((frame) => frame.includes("#invite=")),
      false,
    );
  } finally {
    await context.close();
  }
});

test("same-document invite link bypasses Blazor navigation without disclosing its fragment", async () => {
  const context = await browser.newContext();
  try {
    const { page, frames } = await openGuest(context, "link-first-invite");
    await page.evaluate(() => {
      const link = document.createElement("a");
      link.href = "/join#invite=link-reopened-invite";
      link.textContent = "Reopen test invitation";
      document.querySelector("main").append(link);
    });
    await page.getByRole("link", { name: "Reopen test invitation" }).click();
    await page.waitForFunction(() => !location.hash && document.querySelector("[data-send-button]")?.disabled === false);
    assert.ok(invitations.includes("link-reopened-invite"));
    assert.equal(
      frames.some((frame) => frame.includes("#invite=")),
      false,
    );
  } finally {
    await context.close();
  }
});

test("shared disconnect presentation pauses user and guest previews and keeps notices inline", async () => {
  for (const [role, failure] of [
    ["user", "reconnect"],
    ["guest", "reconnect"],
    ["user", "fatal"],
    ["guest", "fatal"],
  ]) {
    const context = role === "user" ? null : await browser.newContext();
    const view =
      role === "user"
        ? await openAdmin(
            userState(deviceState({ isConnected: true, transmittedSettings: { pattern: 0, speed: 5, stroke: 10, depth: 20, sensation: 50 } })),
          )
        : await openGuest(context, "preview-disconnect");
    try {
      const { page } = view;
      if (role === "guest") {
        await selectSpeed(page, 50);
      }
      await page.locator(".preview-carriage animate").waitFor({ state: "attached" });
      const settings = await page.locator(".preview-rail").getAttribute("aria-label");
      assert.equal(await page.locator(".preview-rail").evaluate((rail) => rail.animationsPaused()), false);
      await page.evaluate((failure) => {
        if (failure === "fatal") {
          document.getElementById("blazor-error-ui").style.display = "block";
          return;
        }
        const notice = document.getElementById("components-reconnect-modal");
        notice.classList.remove("components-reconnect-hide");
        notice.classList.add("components-reconnect-failed");
        notice.dispatchEvent(new CustomEvent("components-reconnect-state-changed", { detail: { state: "failed" } }));
      }, failure);
      const notice =
        role === "user" ? page.locator(failure === "fatal" ? "#blazor-error-ui" : ".rw-reconnect-failed") : page.getByTestId("guest-disconnected");
      await notice.waitFor({ state: "visible" });
      assert.equal(await page.locator(".preview-rail").evaluate((rail) => rail.animationsPaused()), true);
      assert.equal(await page.locator(".preview-rail").getAttribute("aria-label"), settings);
      assert.equal(await page.locator(".connection-sensitive").evaluate((element) => getComputedStyle(element).filter), "grayscale(1)");
      assert.equal(await notice.evaluate((element) => getComputedStyle(element).position), "static");
      assert.deepEqual(await filtersOnAncestors(notice), [], "the loss notice must retain its semantic colour");
      assert.equal(await notice.evaluate((element) => element.matches("rw-alert.rw-inline-notification[variant='danger']")), true);
      assert.equal(await page.locator("rw-alert:visible").filter({ hasText: "Connection lost." }).count(), 1);
      if (role === "user") assert.deepEqual(await filtersOnAncestors(page.getByTestId("device-connection")), ["grayscale(1)"]);
      if (role === "guest") assert.equal(await page.locator("[data-send-button]").isDisabled(), true);
      for (const viewport of viewports) {
        await page.setViewportSize(viewport);
        const notification = await notice.boundingBox();
        const main = await page.locator("main").boundingBox();
        assert.ok(notification.x >= main.x && notification.x + notification.width <= main.x + main.width + 1);
        assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
        if (process.env.GUEST_SCREENSHOT_DIR) {
          await page.screenshot({
            path: join(process.env.GUEST_SCREENSHOT_DIR, `ossm-${role}-${failure}-disconnected-${viewport.width}.png`),
            fullPage: true,
          });
        }
      }
      assert.deepEqual(view.errors, []);
    } finally {
      if (role === "user") await closeAdmin(view.context);
      else await context.close();
    }
  }
});

test("user API loss retains a greyscale last state with a standard inline notice", async () => {
  const { context, page, errors } = await openAdmin(
    userState(deviceState({ isConnected: true, transmittedSettings: { pattern: 0, speed: 5, stroke: 10, depth: 20, sensation: 50 } })),
  );
  try {
    assert.equal(await page.locator(".preview-carriage animate").count(), 1);
    for (const socket of sockets.clients) socket.close(1000);
    const notice = page.getByRole("alert").filter({ hasText: "Unable to connect to the user service." });
    await notice.waitFor({ state: "visible" });
    assert.equal(await page.getByTestId("device-connection").count(), 1);
    assert.deepEqual(await filtersOnAncestors(page.getByTestId("device-connection")), ["grayscale(1)"]);
    assert.equal(await page.locator(".user-grid").evaluate((element) => getComputedStyle(element).filter), "grayscale(1)");
    assert.equal(await page.locator(".preview-carriage animate").count(), 0);
    assert.equal(await notice.evaluate((element) => getComputedStyle(element).position), "static");
    assert.deepEqual(await filtersOnAncestors(notice), []);
    assert.equal(
      await page.locator(".preview-rail").getAttribute("aria-label"),
      "Simple stroke preview at 5% speed, 10% stroke, 20% depth, and 50% sensation",
    );
    assert.deepEqual(errors, []);
  } finally {
    await closeAdmin(context);
  }
});

test("guest device connection loss stops and greys the selected preview until positive recovery", async () => {
  const context = await browser.newContext();
  const invitation = "preview-device-loss";
  const session = { state: guestState(), submissions: [] };
  sessions.set(invitation, session);
  try {
    const { page, errors } = await openGuest(context, invitation);
    await selectSpeed(page, 50);
    await page.locator(".preview-carriage animate").waitFor({ state: "attached" });
    const settings = await page.locator(".preview-rail").getAttribute("aria-label");
    for (const device of [null, { isConnected: false }, { isConnecting: true }, { isConnected: false, connectionFailed: true }]) {
      session.state.device = device === null ? null : { ...guestState().device, ...device };
      await page.locator(".preview-carriage animate").waitFor({ state: "detached" });
      assert.deepEqual(await filtersOnAncestors(page.locator(".guest-interface")), ["grayscale(1)"]);
      assert.equal(await page.locator(".preview-rail").getAttribute("aria-label"), settings);
      assert.equal(await page.locator('[data-input="speed"]').inputValue(), "50");
      assert.equal(await page.getByTestId("guest-disconnected").isVisible(), false, "device loss must not create a service-loss notice");
      if (device === null || device.connectionFailed) assert.equal(await page.locator("[data-send-button]").isDisabled(), true);
      session.state.device = guestState().device;
      await page.locator(".preview-carriage animate").waitFor({ state: "attached" });
      assert.deepEqual(await filtersOnAncestors(page.locator(".pattern-preview")), []);
    }
    assert.equal(session.submissions.length, 0, "device recovery must not submit settings");
    assert.deepEqual(errors, []);
  } finally {
    await context.close();
    sessions.delete(invitation);
  }
});

test("guest initial device states grey the preview without inventing service loss", async () => {
  for (const [name, device] of [
    ["unknown", null],
    ["disconnected", deviceState()],
    ["connecting", deviceState({ isConnecting: true })],
    ["failed", deviceState({ connectionFailed: true })],
  ]) {
    const context = await browser.newContext();
    const invitation = `initial-device-${name}`;
    sessions.set(invitation, { state: guestState({ device }) });
    try {
      const { page, errors } = await openGuest(context, invitation, device !== null && !device.connectionFailed);
      assert.deepEqual(await filtersOnAncestors(page.locator(".guest-interface")), ["grayscale(1)"], name);
      assert.equal(await page.locator(".preview-carriage animate").count(), 0);
      assert.equal(await page.getByTestId("guest-disconnected").isVisible(), false);
      assert.deepEqual(errors, []);
    } finally {
      await context.close();
      sessions.delete(invitation);
    }
  }
});

test("component module import failure shows a standard fallback notice", async () => {
  const context = await browser.newContext();
  let bootstrapLoaded = false;
  let componentImportAborted = false;
  try {
    await context.addInitScript(() => {
      document.addEventListener(
        "readystatechange",
        () => {
          const bootstrap = document.getElementById("guest-start");
          const controller = new URL(bootstrap.dataset.controller, document.baseURI);
          controller.search = "?bootstrap-test";
          bootstrap.dataset.controller = controller.href;
        },
        { once: true },
      );
    });
    await context.route(/\/guest-session(?:\.[^/]+)?\.js(?:\?.*)?$/, async (route) => {
      if (new URL(route.request().url()).search === "?bootstrap-test") {
        bootstrapLoaded = true;
        await route.continue();
      } else {
        componentImportAborted = true;
        await route.abort("failed");
      }
    });
    const page = await context.newPage();
    page.setDefaultTimeout(10_000);
    await page.goto(`${origin}/join#invite=module-import-failure`);
    const notice = page.getByRole("alert").filter({ hasText: "Connection lost." });
    await notice.waitFor({ state: "visible" });
    assert.equal(bootstrapLoaded, true);
    assert.equal(componentImportAborted, true, "the component import must fail independently of bootstrap");
    assert.equal(await page.locator("html[data-guest-connection-lost]").count(), 0);
    assert.equal(await page.getByTestId("guest-disconnected").isVisible(), false);
    assert.equal(await page.locator(".guest-session, .preview-rail").count(), 0);
    assert.equal(await page.getByTestId("guest-invalid").count(), 0);
    assert.equal(await page.locator("rw-alert:visible").count(), 1);
    assert.equal(await notice.evaluate((element) => element.matches("rw-alert.rw-inline-notification[variant='danger']")), true);
    assert.equal(await notice.evaluate((element) => getComputedStyle(element).position), "static");
    assert.deepEqual(await filtersOnAncestors(notice), []);
    for (const viewport of viewports) {
      await page.setViewportSize(viewport);
      assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
      if (process.env.GUEST_SCREENSHOT_DIR) {
        await page.screenshot({ path: join(process.env.GUEST_SCREENSHOT_DIR, `ossm-guest-import-loss-${viewport.width}.png`), fullPage: true });
      }
    }
  } finally {
    await context.close();
  }
});

test("initial API loss does not fabricate a joined guest view", async () => {
  const context = await browser.newContext();
  const invitation = "initial-api-loss";
  sessions.set(invitation, { pollFailure: "unexpected-rejection" });
  try {
    const page = await context.newPage();
    await page.goto(`${origin}/join#invite=${invitation}`);
    await page.getByTestId("guest-disconnected").waitFor({ state: "visible" });
    assert.equal(await page.locator(".guest-session, .preview-rail").count(), 0);
    assert.equal(await page.getByTestId("guest-invalid").count(), 0);
    assert.equal(await page.locator("rw-alert:visible").filter({ hasText: "Connection lost." }).count(), 1);
  } finally {
    await context.close();
    sessions.delete(invitation);
  }
});

test("API transport loss displays recovery and persistently disables browser submissions", async () => {
  const context = await browser.newContext();
  try {
    const { page } = await openGuest(context, "transport-test-invite");
    await selectSpeed(page, 50);
    await page.locator(".preview-carriage animate").waitFor({ state: "attached" });
    const settings = await page.locator(".preview-rail").getAttribute("aria-label");
    await page.evaluate(() => {
      window.originalSend = document.querySelector("[data-send-button]");
    });
    for (const socket of sockets.clients) socket.close(1000);
    const notice = page.getByTestId("guest-disconnected");
    await notice.waitFor({ state: "visible" });
    await page.locator(".preview-carriage animate").waitFor({ state: "detached" });
    assert.equal(await page.locator("[data-stop-button]").isDisabled(), true);
    assert.equal(await page.getByRole("button", { name: "Leave session" }).isDisabled(), true);
    assert.equal(await page.locator(".preview-rail").getAttribute("aria-label"), settings);
    assert.deepEqual(await filtersOnAncestors(page.locator(".guest-interface")), ["grayscale(1)"]);
    assert.deepEqual(await filtersOnAncestors(notice), []);
    assert.match(await notice.textContent(), /Connection lost.*invitation link again/s);
    assert.equal(await page.evaluate(() => sessionStorage.getItem("ossm-guest-connection-lost")), "true");
    const freshLayout = await page.request.get(`${origin}/join`);
    await notice.evaluate(
      (element, html) => {
        const replacement = new DOMParser().parseFromString(html, "text/html").querySelector('[data-testid="guest-disconnected"]');
        element.replaceWith(document.importNode(replacement, true));
      },
      await freshLayout.text(),
    );
    assert.equal(await notice.isVisible(), true, "a fresh layout render must not hide the persistent recovery notice");
    const before = submissions.length;
    await page.evaluate(async () => {
      const controller = document.getElementById("guest-start").dataset.controller;
      const module = await import(new URL(controller, document.baseURI).href);
      module.updateState({ joined: true, ready: true });
      window.originalSend.click();
    });
    assert.equal(await page.evaluate(() => window.originalSend.disabled), true);
    assert.equal(submissions.length, before);
    for (const viewport of viewports) {
      await page.setViewportSize(viewport);
      assert.equal(await notice.isVisible(), true);
      assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
      if (process.env.GUEST_SCREENSHOT_DIR) {
        await page.screenshot({ path: join(process.env.GUEST_SCREENSHOT_DIR, `ossm-guest-api-loss-${viewport.width}.png`), fullPage: true });
      }
    }
    await page.reload();
    await notice.waitFor({ state: "visible" });
    assert.equal(await page.locator("[data-send-button]:not([disabled])").count(), 0);
  } finally {
    await context.close();
  }
});
