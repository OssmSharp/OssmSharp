import assert from "node:assert/strict";
import { test } from "node:test";
import { attachGuest, connectionHarness, connectionSource, loadModule } from "./fixtures/connection-harness.mjs";

for (const role of ["user", "guest"]) {
  for (const signal of ["reconnect", "fatal"]) {
    test(`${role} ${signal} signals pause once and retain state`, async () => {
      const h = connectionHarness();
      const module = await loadModule(connectionSource, h.context);
      let calls = 0;
      let guest;
      const connection = module.namespace.initialize(role === "guest" ? () => {
        calls++;
        guest.api.disconnect();
      } : undefined);
      if (role === "guest") guest = await attachGuest(h);
      assert.equal(connection.circuit.reconnectionOptions.maxRetries, 0);
      h.reconnect("hide");
      h.observers[0].callback([{ target: { id: "other", style: { display: "block" } } }]);
      h.observers[0].callback([{ target: { id: "blazor-error-ui", style: { display: "none" } } }]);
      assert.equal(h.rail.pauses, 0);
      assert.equal(h.document.documentElement.dataset.connectionLost, undefined);
      if (signal === "reconnect") h.reconnect("show");
      else h.observers[0].callback([{ target: { id: "blazor-error-ui", style: { display: "block" } } }]);
      h.reconnect("failed");
      connection.failed();
      h.reconnect("hide");
      // Shared connectionDown runs once; the guest's own persistent-disconnect endpoint also pauses.
      assert.equal(h.rail.pauses, role === "guest" ? 2 : 1);
      assert.equal(calls, role === "guest" ? 1 : 0);
      assert.equal(h.document.documentElement.dataset.connectionLost, "true");
      assert.equal(h.observers[0].disconnected, true);
      assert.equal(h.classes.has("components-reconnect-hide"), false);
      assert.equal(h.classes.has("components-reconnect-rejected"), true);
      if (guest) {
        assert.equal(guest.controls.every((control) => control.disabled), true);
        assert.equal(guest.storage.get("ossm-guest-connection-lost"), "true");
        assert.equal(guest.elements.get('[data-input="speed"]').value, "0");
        assert.deepEqual(["stroke", "depth", "sensation"].map((name) => guest.elements.get(`[data-input="${name}"]`).value), ["10", "20", "75"]);
        assert.equal(guest.elements.get("[data-pattern]").value, "6");
        guest.api.updateState({ joined: true, ready: true });
        assert.equal(guest.controls.every((control) => control.disabled), true);
        guest.api.dispose();
      }
    });
  }
}
