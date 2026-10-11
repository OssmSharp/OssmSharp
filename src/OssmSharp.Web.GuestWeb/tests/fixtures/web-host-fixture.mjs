import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { randomUUID } from "node:crypto";
import { once } from "node:events";
import { access, mkdtemp, rm } from "node:fs/promises";
import { createServer } from "node:http";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { setTimeout as delay } from "node:timers/promises";
import { fileURLToPath } from "node:url";
import { acceptUpgrade, exchange, hubMessage, readHubMessages, WireSocket } from "./protocol-transport.mjs";

const root = fileURLToPath(new URL("../../../../", import.meta.url));
const adminKey = "A".repeat(43);
const allowedEnvironment = /^(PATH|PATHEXT|SystemRoot|WINDIR|COMSPEC|TMP|TEMP|TMPDIR|LANG|LANGUAGE|LC_.+|TZ|DOTNET_ROOT(?:_.+)?|DOTNET_HOST_PATH|DOTNET_MULTILEVEL_LOOKUP|DOTNET_ROLL_FORWARD(?:_.+)?|DOTNET_CLI_.+|NUGET_.+)$/;

/** Filters inherited configuration; hosts receive only explicit synthetic OSSM settings. */
export function isolatedEnvironment(inherited, home, settings) {
  return {
    ...Object.fromEntries(Object.entries(inherited).filter(([name]) => allowedEnvironment.test(name))),
    HOME: home,
    DOTNET_CLI_HOME: home,
    DOTNET_ENVIRONMENT: "Development",
    ASPNETCORE_ENVIRONMENT: "Development",
    OTEL_SDK_DISABLED: "true",
    OSSM__Guest__Hostname: "",
    ...settings,
  };
}

async function listen(server) {
  server.listen(0, "127.0.0.1");
  await once(server, "listening");
  return server.address().port;
}

/** Waits for an observable adapter condition with a deadline, not a fixed sleep. */
export async function waitFor(condition, description, milliseconds = 5_000) {
  const signal = AbortSignal.timeout(milliseconds);
  while (!condition()) {
    try {
      await delay(10, undefined, { signal });
    } catch (error) {
      throw new Error(`Deadline waiting for ${description}`, { cause: error });
    }
  }
}

/** Starts isolated real web assemblies with a minimal remote SignalR API collaborator. */
export async function createWebHostFixture({ inherited = process.env, api = {} } = {}) {
  const home = await mkdtemp(join(tmpdir(), "ossm-protocol-"));
  const hosts = [];
  const connections = new Set();
  const sockets = new Set();
  const tokenMap = new Map();
  const sessions = new Map();
  const handshakes = [];
  const calls = [];
  const pending = new Map();
  const faults = [];
  const savedSessions = new Map();
  const redeemedInvitations = new Map();
  let currentInvitation = "isolated-admin-invite";
  let stopRequired = false;
  let disposed = false;
  const validSavedSession = (saved) => saved?.valid && saved.expiresAt > new Date().toISOString();
  function revokeSavedSessions() {
    currentInvitation = `replacement-${randomUUID()}`;
    for (const saved of savedSessions.values()) {
      if (!saved.valid) continue;
      stopRequired = true;
      saved.valid = false;
      saved.owner = null;
      api.revoked?.(saved);
    }
    api.invitationChanged?.(currentInvitation);
  }
  const peer = createServer((req, res) => {
    if (!req.url.startsWith("/hubs/control/v2/negotiate")) {
      res.writeHead(404).end();
      return;
    }
    const authorization = req.headers.authorization;
    handshakes.push({ kind: "negotiate", authorization, path: req.url });
    const invitationAuthorized = authorization?.startsWith("Bearer ") && !redeemedInvitations.has(authorization.slice("Bearer ".length));
    const savedAuthorized = authorization?.startsWith("Session ") && validSavedSession(savedSessions.get(authorization.slice("Session ".length)));
    if (!(invitationAuthorized || savedAuthorized || authorization === `ServiceKey ${adminKey}`) || api.authorize?.(authorization) === false) {
      res.writeHead(401).end();
      return;
    }
    const token = randomUUID();
    tokenMap.set(token, authorization);
    res.writeHead(200, { "Content-Type": "application/json" });
    res.end(JSON.stringify({
      negotiateVersion: 1, connectionId: token, connectionToken: token,
      availableTransports: [{ transport: "WebSockets", transferFormats: ["Text", "Binary"] }],
    }));
  });
  peer.on("connection", (socket) => {
    sockets.add(socket);
    socket.on("close", () => sockets.delete(socket));
  });
  peer.on("upgrade", (req, socket, head) => {
    const token = new URL(req.url, "http://fixture.invalid").searchParams.get("id");
    const authorization = req.headers.authorization;
    handshakes.push({ kind: "upgrade", authorization, path: req.url });
    if (!tokenMap.has(token) || tokenMap.get(token) !== authorization) {
      socket.end("HTTP/1.1 401 Unauthorized\r\nConnection: close\r\nContent-Length: 0\r\n\r\n");
      return;
    }
    tokenMap.delete(token);
    acceptUpgrade(req, socket);
    const wire = new WireSocket(socket, { head });
    wire.on("fault", (error) => faults.push(error));
    const saved = authorization.startsWith("Session ") ? savedSessions.get(authorization.slice("Session ".length)) : null;
    const session = {
      authorization, wire, calls: [], closed: false, saved,
      invitationToken: saved?.invitationToken ?? (authorization.startsWith("Bearer ") ? authorization.slice("Bearer ".length) : null),
    };
    sessions.set(token, session);
    socket.on("close", () => {
      session.closed = true;
      for (const [key, value] of pending) if (value.session === session) pending.delete(key);
      if (saved?.owner === session) {
        saved.owner = null;
        api.detached?.(saved, session);
      }
    });
    let text = "";
    wire.on("message", (data) => {
      text += data.toString();
      let separator;
      while ((separator = text.indexOf("\u001e")) >= 0) {
        const message = JSON.parse(text.slice(0, separator));
        text = text.slice(separator + 1);
        if (message.protocol) {
          wire.send("{}\u001e");
          continue;
        }
        if (message.type === 6) {
          wire.send('{"type":6}\u001e');
          continue;
        }
        if (message.type !== 1) continue;
        const call = { session, target: message.target, arguments: message.arguments };
        calls.push(call);
        session.calls.push(call);
        const reply = (result) => {
          if (["Stop", "EndGuest", "RefreshInvitation", "LeaveSavedSession", "Leave"].includes(message.target) && result.accepted) {
            stopRequired = false;
            for (const saved of savedSessions.values()) saved.stopPending = false;
          }
          wire.send(`${JSON.stringify({ type: 3, invocationId: message.invocationId, result })}\u001e`);
        };
        const reject = () => wire.send(`${JSON.stringify({ type: 3, invocationId: message.invocationId, error: "Isolated API failure" })}\u001e`);
        if (authorization.startsWith("Bearer ") && message.target !== "Redeem") {
          reject();
          continue;
        }
        if (saved && !validSavedSession(saved)) {
          if (message.target === "GetSavedSession") reply(null);
          else if (message.target === "GetGuestState") reply({ joined: false, controlAllowed: false, device: null });
          else reply({ accepted: false, code: "invalid-guest-session" });
          continue;
        }
        if (["GetGuestState", "Submit", "Renew", "GuestStop", "GuestResetPosition", "Leave"].includes(message.target) && saved?.owner !== session) {
          reply({ accepted: false, code: "guest-not-joined" });
          continue;
        }
        if (message.target === "LeaveSavedSession" && saved?.owner) {
          reply({ accepted: false, code: "guest-already-joined" });
          continue;
        }
        if (message.target === "LeaveSavedSession" || message.target === "Leave") {
          stopRequired = true;
          saved.valid = false;
          saved.owner = null;
          api.revoked?.(saved);
        } else if (message.target === "RefreshInvitation" || message.target === "EndGuest") {
          assert.equal(authorization, `ServiceKey ${adminKey}`);
          revokeSavedSessions();
        }
        if (api.invoke?.(call, { reply, reject }) === true) continue;
        if (message.target === "Redeem") {
          const request = message.arguments[0];
          if (stopRequired || request.invitationToken !== session.invitationToken || redeemedInvitations.has(request.invitationToken)) {
            reply({ correlation: request.correlation, accepted: false, code: "invalid-invitation" });
            continue;
          }
          const credential = randomUUID();
          const admission = {
            invitationToken: request.invitationToken, credential, valid: true, owner: null, stopPending: false,
            expiresAt: new Date(Date.now() + 24 * 60 * 60 * 1000).toISOString(),
          };
          savedSessions.set(credential, admission);
          redeemedInvitations.set(request.invitationToken, admission);
          api.admitted?.(admission);
          reply({ correlation: request.correlation, accepted: true, code: "invitation-redeemed", sessionToken: credential, expiresAt: admission.expiresAt });
        } else if (message.target === "GetSavedSession") {
          reply({ connected: saved.owner !== null, expiresAt: saved.expiresAt, canAttach: saved.owner === null && !saved.stopPending && !stopRequired });
        } else if (message.target === "Join") {
          if (saved.owner || saved.stopPending || stopRequired) {
            reply({ accepted: false, code: saved.owner ? "guest-already-joined" : "device-not-ready" });
          } else {
            saved.owner = session;
            reply({ accepted: true, code: "joined" });
          }
        } else if (message.target === "GetGuestState") {
          reply({
            joined: true, controlAllowed: true, controlExpiresAt: null, canResumeAfterStop: false, sessionExpiresAt: saved.expiresAt,
            device: { pattern: 0, speed: 0, stroke: 10, depth: 20, sensation: 50, isArmed: true, isConnected: true, connectionFailed: false },
          });
        } else if (message.target === "GetUserState") {
          reply({ invitation: { token: currentInvitation }, guestJoined: true, guestConnected: true, guestControlAllowed: false, controlExpiresAt: null, device: null });
        } else if (message.target === "SetPreviewDimensions" && authorization === `ServiceKey ${adminKey}`) {
          reply({ accepted: true, code: "preview-updated" });
        } else if (["Leave", "LeaveSavedSession", "RefreshInvitation", "EndGuest"].includes(message.target)) {
          reply({ accepted: true, code: "accepted" });
        } else if (message.target === "GuestStop") {
          pending.set(`${token}:${message.invocationId}`, { session, reply });
        } else {
          faults.push(new Error(`Unsupported API collaborator method: ${message.target}`));
          socket.destroy();
        }
      }
    });
  });

  const fixture = {
    home, hosts, connections, sockets, tokenMap, sessions, savedSessions, handshakes, calls, pending, faults, adminKey,
    async startHost(kind, { publicOrigin = "", settings = {} } = {}) {
      const reservation = createServer();
      const port = await listen(reservation);
      await new Promise((resolve, reject) => reservation.close((error) => error ? reject(error) : resolve()));
      const origin = `http://127.0.0.1:${port}`;
      const projectName = kind === "guest" ? "OssmSharp.Web.GuestWeb" : "OssmSharp.Web.AdminWeb";
      const hostHome = await mkdtemp(join(home, `${kind}-`));
      const env = isolatedEnvironment(inherited, hostHome, {
        OSSM__ControlClient__EndpointUri: fixture.apiOrigin,
        OSSM__ControlClient__ServiceKey: kind === "guest" ? "" : adminKey,
        OSSM__ControlClient__KeepAliveSeconds: "1",
        OSSM__ControlClient__ServerTimeoutSeconds: "3",
        OSSM__ControlClient__RequestTimeoutSeconds: "2",
        ...(kind === "guest"
          ? { OSSM__Guest__GuestPort: String(port), OSSM__Guest__Hostname: publicOrigin }
          : { OSSM__Admin__ListenPort: String(port), OSSM__Admin__GuestHostname: "https://guest.test" }),
        ...settings,
      });
      const assembly = join(root, ".artifacts", "bin", projectName.toLowerCase(), "debug", `${projectName}.dll`);
      await access(assembly);
      const child = spawn("dotnet", [assembly], { cwd: join(root, "src", projectName), env, stdio: ["ignore", "pipe", "pipe"] });
      const exited = once(child, "exit");
      // Host output can contain capabilities. Keep it out of test diagnostics.
      child.stdout.resume();
      child.stderr.resume();
      const host = { kind, origin, publicOrigin, port, home: hostHome, env, child, exited };
      hosts.push(host);
      const controller = new AbortController();
      const deadline = setTimeout(() => controller.abort(), 15_000);
      try {
        await Promise.race([
          (async () => {
            while (true) {
              try {
                const response = await fetch(`${origin}/health`, { signal: AbortSignal.any([controller.signal, AbortSignal.timeout(1_000)]), redirect: "error" });
                await response.arrayBuffer();
                if (response.status === 200) return;
              } catch (error) {
                if (controller.signal.aborted) throw new Error("Web host readiness deadline exceeded", { cause: error });
              }
              await delay(50, undefined, { signal: controller.signal });
            }
          })(),
          exited.then(([code, signal]) => { throw new Error(`Web host exited before readiness (code ${code}, signal ${signal})`); }),
        ]);
      } finally {
        clearTimeout(deadline);
        controller.abort();
      }
      return host;
    },
    async dispose() {
      if (disposed) return;
      disposed = true;
      const errors = [];
      for (const wire of connections) {
        try { await wire.dispose(); } catch (error) { errors.push(error); }
      }
      connections.clear();
      for (const host of hosts) {
        if (host.child.exitCode !== null || host.child.signalCode !== null) continue;
        const deadline = setTimeout(() => host.child.kill("SIGKILL"), 5_000);
        try {
          host.child.kill("SIGTERM");
          await host.exited;
        } catch (error) {
          errors.push(error);
        } finally {
          clearTimeout(deadline);
        }
      }
      const closed = [...sockets].map((socket) => new Promise((resolve) => socket.once("close", resolve)));
      for (const socket of sockets) socket.destroy();
      await Promise.all(closed);
      if (peer.listening) await new Promise((resolve, reject) => peer.close((error) => error ? reject(error) : resolve()));
      tokenMap.clear();
      sessions.clear();
      pending.clear();
      savedSessions.clear();
      redeemedInvitations.clear();
      handshakes.length = 0;
      calls.length = 0;
      await rm(home, { recursive: true, force: true });
      if (errors.length) throw new AggregateError(errors, "Fixture cleanup failed");
    },
    get listening() { return peer.listening; },
  };
  try {
    fixture.apiOrigin = `http://127.0.0.1:${await listen(peer)}`;
    return fixture;
  } catch (error) {
    await fixture.dispose();
    throw error;
  }
}

async function bootstrapGuestCookie(host, invitation, headers) {
  const cookies = new Map();
  const request = async (path, options = {}) => {
    const response = await exchange(host.origin, path, {
      ...options,
      headers: { ...headers, Cookie: [...cookies].map(([name, value]) => `${name}=${value}`).join("; "), ...options.headers },
    });
    for (const value of response.headers["set-cookie"] ?? []) {
      const [name, token] = value.split(";", 1)[0].split("=");
      if (token) cookies.set(name, token);
      else cookies.delete(name);
    }
    return { ...response, state: JSON.parse(response.body) };
  };
  let response = await request("/guest-session");
  if (invitation && response.state.code === "no-session") {
    response = await request("/guest-session/redeem", {
      method: "POST",
      headers: {
        Origin: host.publicOrigin || host.origin,
        "Content-Type": "application/json", "X-CSRF-TOKEN": response.state.requestToken,
      },
      body: JSON.stringify({ token: invitation }),
    });
    if (response.state.code === "saved") response = await request("/guest-session");
  }
  return {
    code: response.state.code,
    headers: { ...headers, Cookie: [...cookies].map(([name, value]) => `${name}=${value}`).join("; ") },
  };
}

/** Supplies JS interop replies on a real circuit; does not simulate or assert browser behavior. */
export async function openCircuit(fixture, host, { invitation = null, headers = {}, path = host.kind === "guest" ? "/join" : "/" } = {}) {
  let sessionCode = "no-session";
  if (host.kind === "guest") {
    const bootstrap = await bootstrapGuestCookie(host, invitation, headers);
    headers = bootstrap.headers;
    sessionCode = bootstrap.code;
  }
  const response = await exchange(host.origin, path, { headers });
  assert.equal(response.status, 200);
  const records = [...response.body.matchAll(/<!--Blazor:(.*?)-->/g)]
    .map((match) => JSON.parse(match[1]))
    .filter((record) => record.type === "server" && record.descriptor);
  assert.ok(records.length > 0, "Real rendered page must supply server component descriptors");
  const negotiate = await exchange(host.origin, "/_blazor/negotiate?negotiateVersion=1", { method: "POST", headers });
  assert.equal(negotiate.status, 200);
  const token = JSON.parse(negotiate.body).connectionToken;
  const upgrade = await exchange(host.origin, `/_blazor?id=${encodeURIComponent(token)}`, { headers, upgrade: true });
  assert.equal(upgrade.status, 101);
  const wire = new WireSocket(upgrade.socket, { masked: true, head: upgrade.head });
  fixture.connections.add(wire);
  wire.on("fault", (error) => fixture.faults.push(error));
  let handshake = true;
  let binary = Buffer.alloc(0);
  const started = Promise.withResolvers();
  const jsCalls = [];
  const renders = [];
  let reference;
  const deadline = setTimeout(() => started.reject(new Error("Circuit start deadline exceeded")), 5_000);
  wire.on("message", (data) => {
    try {
      if (handshake) {
        const separator = data.indexOf(0x1e);
        assert.ok(separator >= 0, "SignalR handshake response required");
        const result = JSON.parse(data.subarray(0, separator).toString());
        assert.equal(result.error, undefined, "Blazor binary protocol must be accepted");
        handshake = false;
        data = data.subarray(separator + 1);
        const currentOrigin = host.publicOrigin || host.origin;
        const descriptors = records.map(({ type, sequence, descriptor }) => ({ type, sequence, descriptor }));
        wire.send(hubMessage([1, {}, "start", "StartCircuit", [`${currentOrigin}/`, `${currentOrigin}${path}`, JSON.stringify(descriptors), ""], []]));
      }
      binary = Buffer.concat([binary, data]);
      const decoded = readHubMessages(binary);
      binary = decoded.rest;
      for (const message of decoded.messages) {
        if (message[0] === 3 && message[2] === "start") {
          assert.equal(message[3], 3, "StartCircuit must return a result rather than an error");
          assert.equal(typeof message[4], "string");
          assert.ok(message[4].length > 0);
          started.resolve();
        }
        if (message[0] !== 1) continue;
        const [, , , target, args] = message;
        if (target === "JS.RenderBatch") {
          renders.push({
            batch: args[0],
            afterBootstrap: jsCalls.includes("getSessionCode"),
            invalidInvitation: args[1].includes(Buffer.from("guest-invalid")),
          });
          wire.send(hubMessage([1, {}, null, "OnRenderCompleted", [args[0], null], []]));
        }
        if (target === "JS.BeginInvokeJS") {
          const [handle, identifier, argumentsJson] = args;
          jsCalls.push(identifier);
          let result = null;
          if (identifier === "import") result = { __jsObjectId: 1 };
          else if (identifier === "getSessionCode") result = sessionCode;
          else if (identifier === "readAttachmentLength") result = JSON.parse(argumentsJson)[0];
          else if (identifier === "attach") reference = JSON.parse(argumentsJson)[1].__dotNetObject;
          else if (identifier === "Blazor._internal.PageTitle.getAndRemoveExistingTitle") result = "";
          else if (![
            "Blazor._internal.attachWebRendererInterop",
            "Blazor._internal.navigationManager.enableNavigationInterception",
            "Blazor._internal.domWrapper.focusBySelector",
            "updateState", "updateConnectionTimer", "disposeConnectionTimer", "writeAttachmentLength", "dispose", "disconnect", "endSession", "disarm",
          ].includes(identifier))
            throw new Error(`Unsupported circuit JS collaborator method: ${identifier}`);
          wire.send(hubMessage([1, {}, null, "EndInvokeJSFromDotNet", [handle, true, JSON.stringify([handle, true, result])], []]));
        }
        if (target === "JS.Error") throw new Error("Real Blazor circuit failed");
      }
    } catch (error) {
      fixture.faults.push(error);
      started.reject(error);
    }
  });
  try {
    wire.send('{"protocol":"blazorpack","version":1}\u001e');
    await started.promise;
    return {
      wire, jsCalls, renders,
      invokeGuestStop() {
        assert.equal(typeof reference, "number", "Production attach must supply the guest object reference");
        wire.send(hubMessage([1, {}, null, "BeginInvokeDotNetFromJS", ["fixture-stop", null, "StopRun", reference, "[]"], []]));
      },
    };
  } catch (error) {
    await wire.dispose();
    throw error;
  } finally {
    clearTimeout(deadline);
  }
}
