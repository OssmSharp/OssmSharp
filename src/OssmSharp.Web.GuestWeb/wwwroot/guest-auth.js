/** Owns same-origin cookie bootstrap and connectivity recovery. Credentials never enter JavaScript. */
export function createSession(onState) {
  let requestToken = null;
  let ended = false;
  let recovery = null;
  let recoveryAbort = null;

  async function snapshot(signal) {
    let response;
    try {
      response = await fetch("/guest-session", { credentials: "same-origin", cache: "no-store", signal });
    } catch (error) {
      if (signal?.aborted) return { code: "cancelled" };
      if (error.name === "TypeError") return { code: "connection-unavailable" };
      throw error;
    }
    if (response.status >= 500 && response.status !== 502) return { code: "connection-unavailable" };
    let state;
    try {
      state = await response.json();
    } catch (error) {
      if (signal?.aborted) return { code: "cancelled" };
      if (error.name === "TypeError") return { code: "connection-unavailable" };
      if (response.status === 502 && error.name === "SyntaxError") return { code: "connection-unavailable" };
      throw error;
    }
    if (response.status === 502 && state?.code !== "invalid-response") return { code: "connection-unavailable" };
    if (typeof state?.code !== "string" || typeof state.requestToken !== "string")
      throw new Error("Invalid guest session response.");
    requestToken = state.requestToken;
    if (state.code === "ready" && (!response.ok || typeof state.connected !== "boolean" || typeof state.canAttach !== "boolean"
      || state.connected && state.canAttach))
      throw new Error("Invalid guest session availability.");
    return state;
  }

  function availability(state) {
    return state.code === "ready"
      ? state.connected ? "session-in-use" : state.canAttach ? "ready" : "stop-pending"
      : state.code;
  }

  async function prepare(invitation) {
    const state = await snapshot();
    if (state.code === "ready" || !invitation) return availability(state);
    if (state.code === "connection-unavailable" || state.code === "invalid-response") return state.code;
    let response;
    try {
      response = await fetch("/guest-session/redeem", {
        method: "POST", credentials: "same-origin",
        headers: { "Content-Type": "application/json", "X-CSRF-TOKEN": requestToken },
        body: JSON.stringify({ token: invitation }),
      });
    } catch (error) {
      if (error.name !== "TypeError") throw error;
      const reconciled = await snapshot();
      return reconciled.code === "ready" ? "reload" : "redemption-unknown";
    }
    if (response.status >= 500) {
      const reconciled = await snapshot();
      return reconciled.code === "ready" ? "reload" : "redemption-unknown";
    }
    const result = await response.json();
    if (typeof result?.code !== "string") throw new Error("Invalid invitation redemption response.");
    if (!response.ok) {
      if (result.code === "redemption-unknown") {
        const reconciled = await snapshot();
        if (reconciled.code === "ready") return "reload";
      }
      return result.code;
    }
    if (result.code !== "saved") throw new Error("Invalid invitation redemption response.");
    const saved = await snapshot();
    return saved.code === "ready" ? "reload"
      : saved.code === "connection-unavailable" ? "connection-unavailable" : "cookie-unavailable";
  }

  function wait(milliseconds, signal) {
    return new Promise((resolve) => {
      const finish = () => { clearTimeout(timer); signal.removeEventListener("abort", finish); resolve(); };
      const timer = setTimeout(finish, milliseconds);
      signal.addEventListener("abort", finish, { once: true });
      if (signal.aborted) finish();
    });
  }

  function recover(reason = "reconnecting") {
    if (ended || recovery) return;
    recoveryAbort = new AbortController();
    const signal = recoveryAbort.signal;
    recovery = (async () => {
      let interval = 1000;
      while (!ended && !signal.aborted) {
        const state = await snapshot(signal);
        if (signal.aborted) return;
        if (state.code === "ready") {
          if (state.canAttach && !state.connected) {
            location.reload();
            return;
          }
          onState(state.connected ? reason === "session-in-use" ? "session-in-use" : "connection-detaching" : "stop-pending");
        } else if (state.code === "connection-unavailable") {
          onState("reconnecting");
        } else {
          ended = true;
          onState(state.code === "no-session" ? "session-ended" : state.code);
          return;
        }
        await wait(interval, signal);
        interval = Math.min(interval * 2, 5000);
      }
    })().catch(() => {
      if (!signal.aborted) {
        ended = true;
        onState("recovery-failed");
        reportError(new Error("Guest session recovery failed unexpectedly."));
      }
    }).finally(() => { recovery = null; });
  }

  async function leave() {
    ended = true;
    recoveryAbort?.abort();
    await recovery;
    const state = await snapshot();
    if (!requestToken) return { ok: false, code: state.code };
    try {
      const response = await fetch("/guest-session/leave", {
        method: "POST", credentials: "same-origin", headers: { "X-CSRF-TOKEN": requestToken },
      });
      const result = await response.json();
      if (result.code === "guest-already-joined") {
        ended = false;
        recover("session-in-use");
      }
      return { ok: response.ok && result.accepted === true, code: result.code ?? "leave-failed" };
    } catch (error) {
      if (error.name !== "TypeError") throw error;
      return { ok: false, code: "connection-unavailable" };
    }
  }

  async function clear() {
    end();
    try {
      const response = await fetch("/guest-session/clear", {
        method: "POST", credentials: "same-origin", headers: { "X-CSRF-TOKEN": requestToken },
      });
      const result = await response.json();
      return { ok: response.ok && result.code === "cookie-cleared", code: result.code ?? "leave-failed" };
    } catch (error) {
      if (error.name !== "TypeError") throw error;
      return { ok: false, code: "connection-unavailable" };
    }
  }

  function end() {
    ended = true;
    recoveryAbort?.abort();
  }

  window.addEventListener("pagehide", () => { ended = true; recoveryAbort?.abort(); }, { once: true });
  return { prepare, recover, leave, clear, end };
}
