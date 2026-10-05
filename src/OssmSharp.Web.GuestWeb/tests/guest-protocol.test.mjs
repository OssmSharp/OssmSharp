import assert from "node:assert/strict";
import { access } from "node:fs/promises";
import { test } from "node:test";
import { exchange, WireSocket } from "./fixtures/protocol-transport.mjs";
import { createWebHostFixture, isolatedEnvironment, openCircuit, waitFor } from "./fixtures/web-host-fixture.mjs";

const publicOrigin = "https://guest.test:8443";
const publicHeaders = { Host: "guest.test:8443", "X-Forwarded-Proto": "https", Origin: publicOrigin };

async function withFixture(context, run, options) {
  const fixture = await createWebHostFixture(options);
  context.after(() => fixture.dispose());
  await run(fixture);
  await fixture.dispose();
  await assertReleased(fixture);
  assert.deepEqual(fixture.faults, []);
}

async function assertReleased(fixture) {
  assert.equal(fixture.listening, false);
  for (const resource of [fixture.sockets, fixture.connections, fixture.pending, fixture.tokenMap, fixture.sessions])
    assert.equal(resource.size, 0);
  assert.equal(fixture.handshakes.length, 0);
  assert.equal(fixture.calls.length, 0);
  assert.ok(fixture.hosts.every(({ child }) => child.exitCode !== null || child.signalCode !== null));
  for (const host of fixture.hosts)
    await assert.rejects(exchange(host.origin, "/health"), { code: "ECONNREFUSED" });
  await assert.rejects(exchange(fixture.apiOrigin, "/health"), { code: "ECONNREFUSED" });
  await assert.rejects(access(fixture.home), { code: "ENOENT" });
}

async function allowedUpgrade(fixture, host, headers) {
  const negotiate = await exchange(host.origin, "/_blazor/negotiate?negotiateVersion=1", { method: "POST", headers });
  assert.equal(negotiate.status, 200);
  const token = JSON.parse(negotiate.body).connectionToken;
  assert.equal(typeof token, "string");
  assert.ok(token.length > 0);
  const result = await exchange(host.origin, `/_blazor?id=${encodeURIComponent(token)}`, { headers, upgrade: true });
  assert.equal(result.status, 101);
  const wire = new WireSocket(result.socket, { masked: true, head: result.head });
  fixture.connections.add(wire);
  await wire.dispose();
  fixture.connections.delete(wire);
}

test("public guest negotiate and upgrades enforce the full host origin and protocol policy", { timeout: 45_000 }, async (context) => {
  await withFixture(context, async (fixture) => {
    const host = await fixture.startHost("guest", { publicOrigin });
    const local = await fixture.startHost("guest");
    for (const origin of [null, "https://evil.example", local.origin, "null", `${publicOrigin}, https://evil.example`, [publicOrigin, "https://evil.example"]]) {
      const headers = { ...publicHeaders };
      if (origin === null) delete headers.Origin;
      else headers.Origin = origin;
      const negotiate = await exchange(host.origin, "/_blazor/negotiate?negotiateVersion=1", { method: "POST", headers });
      assert.equal(negotiate.status, 403, "Unsafe negotiate Origin must be rejected");
      // A GET with actual Upgrade headers, not an ordinary fetch carrying lookalike headers.
      const upgrade = await exchange(host.origin, "/_blazor", { headers, upgrade: true });
      assert.equal(upgrade.status, 403, "Unsafe WebSocket Origin must be rejected");
    }
    for (const headers of [
      { ...publicHeaders, "X-Forwarded-Proto": undefined },
      { ...publicHeaders, "X-Forwarded-Proto": "http" },
      { ...publicHeaders, Host: "guest.test:444" },
    ]) {
      const supplied = Object.fromEntries(Object.entries(headers).filter(([, value]) => value !== undefined));
      assert.equal((await exchange(host.origin, "/join", { headers: supplied })).status, 403);
      assert.equal((await exchange(host.origin, "/_blazor/negotiate?negotiateVersion=1", { method: "POST", headers: supplied })).status, 403);
      assert.equal((await exchange(host.origin, "/_blazor", { headers: supplied, upgrade: true })).status, 403);
    }
    assert.equal((await exchange(host.origin, "/join", { headers: { Host: "evil.example", Origin: publicOrigin, "X-Forwarded-Proto": "https" } })).status, 400);
    assert.equal((await exchange(local.origin, "/join", { headers: publicHeaders })).status, 400);
    const localHeaders = { Origin: host.origin };
    assert.equal((await exchange(host.origin, "/join")).status, 200);
    assert.equal((await exchange(host.origin, "/_blazor/negotiate?negotiateVersion=1", { method: "POST" })).status, 403);
    assert.equal((await exchange(host.origin, "/_blazor/negotiate?negotiateVersion=1", { method: "POST", headers: { Origin: publicOrigin, "X-Forwarded-Proto": "https" } })).status, 403);
    assert.equal((await exchange(host.origin, "/join", { headers: { Origin: "https://evil.example" } })).status, 403);
    assert.equal((await exchange(host.origin, "/join", { headers: { "X-Forwarded-Host": "evil.example", "X-Forwarded-For": "198.51.100.10" } })).status, 200);
    await allowedUpgrade(fixture, host, publicHeaders);
    await allowedUpgrade(fixture, host, localHeaders);
    assert.equal(fixture.handshakes.length, 0, "Denied requests and an unstarted circuit must not reach the remote API");
    assert.equal(fixture.calls.length, 0);
  });
});

test("local admin rejects public requests and both hosts emit established security headers", { timeout: 45_000 }, async (context) => {
  await withFixture(context, async (fixture) => {
    const guest = await fixture.startHost("guest", { publicOrigin });
    const admin = await fixture.startHost("admin");
    assert.equal((await exchange(admin.origin, "/", { headers: { Host: "guest.test", Origin: "https://guest.test", "X-Forwarded-Proto": "https" } })).status, 400);
    assert.equal((await exchange(admin.origin, "/_blazor/negotiate?negotiateVersion=1", { method: "POST", headers: publicHeaders })).status, 400);
    assert.equal((await exchange(admin.origin, "/_blazor", { headers: publicHeaders, upgrade: true })).status, 400);
    assert.equal((await exchange(admin.origin, "/", { headers: { Host: "127.0.0.1:444" } })).status, 403);
    assert.equal((await exchange(admin.origin, "/", { headers: { Origin: publicOrigin } })).status, 403);
    assert.equal((await exchange(admin.origin, "/_blazor/negotiate?negotiateVersion=1", { method: "POST" })).status, 403);
    for (const [host, path, headers, websocketOrigin] of [
      [guest, "/join", publicHeaders, "wss://guest.test:8443"],
      [guest, "/join", {}, `ws://127.0.0.1:${guest.port}`],
      [admin, "/", {}, `ws://127.0.0.1:${admin.port}`],
    ]) {
      const response = await exchange(host.origin, path, { headers });
      assert.equal(response.status, 200);
      assert.equal(response.headers["referrer-policy"], "no-referrer");
      assert.equal(response.headers["x-frame-options"], "DENY");
      assert.equal(response.headers["x-content-type-options"], "nosniff");
      assert.ok(response.headers["cache-control"].split(",").map((value) => value.trim()).includes("no-store"));
      assert.ok(response.headers["content-security-policy"].includes(`connect-src 'self' ${websocketOrigin};`));
      assert.ok(response.headers["content-security-policy"].includes("frame-ancestors 'none'"));
    }
    await allowedUpgrade(fixture, admin, { Origin: admin.origin });
    assert.equal(fixture.handshakes.length, 0);
  });
});

test("fixture traffic keeps invitation and administrator credentials isolated", { timeout: 45_000 }, async (context) => {
  const inherited = {
    ...process.env,
    OSSM__ControlClient__ServiceKey: "inherited-key-must-not-leak",
    OSSM__ControlClient__EndpointUri: "http://untrusted.invalid",
    ASPNETCORE_URLS: "http://0.0.0.0:5000",
  };
  await withFixture(context, async (fixture) => {
    const guest = await fixture.startHost("guest", { publicOrigin });
    const admin = await fixture.startHost("admin");
    const invitation = "isolated-public-invite";
    await openCircuit(fixture, guest, { invitation, headers: publicHeaders });
    await waitFor(() => fixture.calls.some((call) => call.target === "GetGuestState"), "real guest API state request");
    assert.equal(guest.env.OSSM__ControlClient__ServiceKey, "");
    assert.equal(guest.env.OSSM__ControlClient__EndpointUri, fixture.apiOrigin);
    assert.equal(guest.env.ASPNETCORE_URLS, undefined);
    assert.equal(fixture.handshakes.length, 2);
    assert.deepEqual(fixture.handshakes.map(({ kind }) => kind), ["negotiate", "upgrade"]);
    assert.ok(fixture.handshakes.every(({ authorization }) => authorization === `Bearer ${invitation}`), "Both guest handshakes must use only the invitation");
    assert.ok(fixture.calls.find((call) => call.target === "Join").arguments[0].invitationToken === invitation);
    await openCircuit(fixture, admin, { headers: { Origin: admin.origin } });
    await waitFor(() => fixture.calls.some((call) => call.target === "GetUserState"), "real administrator API state request");
    assert.equal(fixture.handshakes.length, 4);
    assert.ok(fixture.handshakes.slice(2).every(({ authorization }) => authorization === `ServiceKey ${fixture.adminKey}`), "Administrator handshakes must use only the synthetic service key");
    assert.equal(admin.env.OSSM__ControlClient__EndpointUri, fixture.apiOrigin);
    const maliciousHeaders = { ...publicHeaders, Authorization: `ServiceKey ${fixture.adminKey}`, "X-Forwarded-Host": "untrusted.invalid" };
    await openCircuit(fixture, guest, { invitation: "second-guest-invite", headers: maliciousHeaders });
    await waitFor(() => fixture.calls.filter((call) => call.target === "GetGuestState").length >= 2, "guest with untrusted browser headers");
    assert.equal(fixture.handshakes.length, 6);
    assert.ok(fixture.handshakes.slice(4).every(({ authorization }) => authorization === "Bearer second-guest-invite"), "Browser credentials and forwarded authority must not replace the guest invitation or configured API destination");
    const previous = fixture.handshakes.length;
    const invalidCircuit = await openCircuit(fixture, guest, {
      invitation: "https://untrusted.invalid/hubs/control/v2",
      headers: maliciousHeaders,
    });
    await waitFor(() => invalidCircuit.renders.some(({ afterInvitation, invalidInvitation }) => afterInvitation && invalidInvitation), "production invalid-invitation render after JS reply");
    await invalidCircuit.wire.dispose();
    fixture.connections.delete(invalidCircuit.wire);
    assert.equal(fixture.handshakes.length, previous, "A malformed invitation cannot become an API destination or use a browser's administrator credential");
  }, { inherited });
});

test("isolated hosts and sessions clean up without sharing invitation or disconnect state", { timeout: 60_000 }, async (context) => {
  const polluted = {
    PATH: process.env.PATH, HOME: "/not-the-fixture-home",
    OSSM__ControlClient__ServiceKey: "untrusted", ASPNETCORE_URLS: "http://0.0.0.0:5000",
    OTEL_EXPORTER_OTLP_ENDPOINT: "http://untrusted.invalid", PLAYWRIGHT_MODULE: "not-a-dependency",
  };
  const env = isolatedEnvironment(polluted, "/isolated-home", { OSSM__ControlClient__ServiceKey: "" });
  assert.deepEqual(Object.keys(env).sort(), [
    "ASPNETCORE_ENVIRONMENT", "DOTNET_CLI_HOME", "DOTNET_ENVIRONMENT", "HOME",
    "OSSM__ControlClient__ServiceKey", "OSSM__Guest__Hostname", "OTEL_SDK_DISABLED", "PATH",
  ].sort());
  assert.equal(env.HOME, "/isolated-home");
  assert.equal(env.DOTNET_CLI_HOME, "/isolated-home");
  const first = await createWebHostFixture();
  context.after(() => first.dispose());
  const second = await createWebHostFixture();
  context.after(() => second.dispose());
  const firstHost = await first.startHost("guest");
  const secondHost = await second.startHost("guest");
  assert.notEqual(first.home, second.home);
  assert.notEqual(firstHost.home, secondHost.home);
  assert.notEqual(first.apiOrigin, second.apiOrigin);
  assert.notEqual(firstHost.origin, secondHost.origin);
  assert.notEqual(first.tokenMap, second.tokenMap);
  const a = await openCircuit(first, firstHost, { invitation: "first-isolated-invite", headers: { Origin: firstHost.origin } });
  await openCircuit(second, secondHost, { invitation: "second-isolated-invite", headers: { Origin: secondHost.origin } });
  await waitFor(() => first.calls.some((call) => call.target === "GetGuestState") && second.calls.some((call) => call.target === "GetGuestState"), "both isolated guest sessions");
  assert.ok(first.handshakes.every(({ authorization }) => authorization === "Bearer first-isolated-invite"));
  assert.ok(second.handshakes.every(({ authorization }) => authorization === "Bearer second-isolated-invite"));
  await waitFor(() => a.jsCalls.includes("attach"), "real guest object-reference attachment");
  a.invokeGuestStop();
  await waitFor(() => first.pending.size === 1, "controlled pending GuestStop reply");
  await a.wire.dispose();
  await waitFor(() => [...first.sessions.values()].every((session) => session.closed), "guest circuit disposal closes its API socket");
  assert.equal(first.pending.size, 0, "Circuit teardown releases its pending API reply");
  assert.ok([...second.sessions.values()].every((session) => !session.closed), "Disconnecting one fixture must not disconnect another");
  assert.equal((await exchange(secondHost.origin, "/health")).status, 200);
  // A startup failure must retain process ownership and remain cleanable.
  await assert.rejects(first.startHost("guest", { settings: { OSSM__Guest__GuestPort: "-1" } }), /exited before readiness/);
  await first.dispose();
  await second.dispose();
  for (const fixture of [first, second]) {
    await assertReleased(fixture);
    assert.deepEqual(fixture.faults, []);
  }
});
