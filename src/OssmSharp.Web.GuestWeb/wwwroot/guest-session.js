const disconnectedKey = "ossm-guest-connection-lost";
let invitation = captureInvitation();
let controller = null;
let guestRoot = null;
let disconnected = sessionStorage.getItem(disconnectedKey) === "true";

function captureInvitation() {
  const fragment = location.hash;
  history.replaceState(null, "", location.pathname);
  if (
    !fragment ||
    fragment.length > 512 ||
    !/^\/join\/[^/]+\/?$/.test(location.pathname)
  )
    return null;
  const values = new URLSearchParams(fragment.slice(1));
  const secret = values.get("invite");
  const valid =
    [...values.keys()].length === 1 && secret?.length <= 128 && secret.trim();
  if (valid) sessionStorage.removeItem(disconnectedKey);
  return valid ? secret : null;
}

export function takeInvitation() {
  const value = invitation;
  invitation = null;
  return value;
}
export function attach(root, reference) {
  controller?.destroy();
  guestRoot = root;
  controller = new Controls(root, reference);
  if (disconnected) {
    showDisconnect();
    controller.destroy();
  }
}
export function updateAuthority(authority) {
  controller?.updateAuthority(authority);
}
export function disarm() {
  controller?.disarm();
}
export function dispose() {
  invitation = null;
  controller?.destroy();
  controller = null;
  guestRoot = null;
}
export function disconnect() {
  disconnected = true;
  sessionStorage.setItem(disconnectedKey, "true");
  showDisconnect();
  dispose();
}
export function restoreDisconnected() {
  if (disconnected) showDisconnect();
}

function showDisconnect() {
  document.documentElement.dataset.guestConnectionLost = "true";
  guestRoot?.setAttribute("data-connection-lost", "true");
  document
    .querySelector("main > [data-testid='guest-disconnected']")
    ?.removeAttribute("hidden");
}

class Controls {
  constructor(root, reference) {
    this.root = root;
    this.reference = reference;
    this.listeners = new AbortController();
    this.names = ["speed", "stroke", "depth"];
    this.inputs = Object.fromEntries(
      this.names.map((name) => [
        name,
        root.querySelector(`[data-input="${name}"]`),
      ]),
    );
    this.sendButton = root.querySelector("[data-send-button]");
    this.autoSendToggle = root.querySelector("[data-auto-send]");
    this.error = null;
    this.lastDisplayPayload = null;
    this.pendingDisplay = null;
    this.displayInFlight = false;
    this.ready = false;
    this.joined = false;
    this.active = false;
    this.destroyed = false;
    this.inFlight = false;
    this.pendingStop = false;
    this.stopping = false;
    this.grant = null;
    this.expiresAt = 0;
    this.sequence = 0;
    this.sent = null;
    this.pending = null;
    this.current = null;
    this.awaitedSequence = 0;
    this.confirmed = false;
    this.userSend = false;
    this.on(this.sendButton, "click", () => this.commitSelection());
    this.on(root.querySelector("[data-stop-button]"), "click", () =>
      this.requestStop(),
    );
    this.on(this.autoSendToggle, "change", () => {
      if (this.autoSendToggle.checked) this.commitSelection();
      else {
        this.pending = null;
        this.userSend = false;
        if (!this.sent) this.active = false;
        this.render();
      }
    });
    for (const input of Object.values(this.inputs))
      this.on(input, "input", () => {
        this.constrainStroke();
        if (this.autoSendToggle.checked && this.canSend())
          this.commitSelection();
        this.render();
      });
    this.on(window, "blur", () => this.requestStop());
    this.on(document, "visibilitychange", () => {
      if (document.visibilityState !== "visible") this.requestStop();
    });
    this.on(window, "pagehide", () => this.destroy());
    this.observer = new MutationObserver(() => {
      if (!root.isConnected) this.destroy();
    });
    this.observer.observe(document.body, { childList: true, subtree: true });
    this.interval = setInterval(() => this.tick(), 150);
    this.constrainStroke();
    this.render();
  }

  on(target, event, action) {
    target.addEventListener(event, action, { signal: this.listeners.signal });
  }
  canSend() {
    return (
      !this.destroyed &&
      this.joined &&
      this.ready &&
      this.grant &&
      performance.now() < this.expiresAt &&
      document.visibilityState === "visible" &&
      !this.pendingStop &&
      !this.stopping
    );
  }
  constrainStroke() {
    const depth = Number(this.inputs.depth.value);
    this.inputs.stroke.max = String(depth);
    if (Number(this.inputs.stroke.value) > depth)
      this.inputs.stroke.value = String(depth);
  }
  selected() {
    return {
      pattern: 0,
      sensation: 50,
      ...Object.fromEntries(
        this.names.map((name) => [name, Number(this.inputs[name].value)]),
      ),
    };
  }
  commitSelection() {
    if (!this.canSend()) return;
    this.pending = this.selected();
    this.userSend = true;
    this.active = true;
    this.error = null;
    this.render();
  }
  disarm() {
    this.active = false;
    this.pending = null;
    this.userSend = false;
    this.autoSendToggle.checked = false;
  }
  requestStop() {
    this.disarm();
    if (this.joined && !this.destroyed) {
      this.stopping = true;
      this.pendingStop = true;
      this.flushStop();
    }
    this.render();
  }
  updateAuthority(authority) {
    if (this.destroyed) return;
    this.joined = authority?.joined === true;
    this.ready = authority?.ready === true;
    this.current = authority?.fresh ? authority.current : null;
    const grant = authority?.grant;
    if (
      grant &&
      this.ready &&
      this.joined &&
      typeof grant.id === "string" &&
      grant.id.length > 0 &&
      typeof grant.epoch === "string" &&
      grant.epoch.length > 0 &&
      Number.isSafeInteger(grant.policyVersion) &&
      grant.policyVersion === authority.policyVersion &&
      Number.isSafeInteger(grant.validForMs) &&
      grant.validForMs > 0
    ) {
      if (this.grant?.epoch !== grant.epoch) {
        this.disarm();
        this.sent = null;
        this.awaitedSequence = 0;
        this.confirmed = false;
        this.sequence = 0;
      }
      const expires = performance.now() + grant.validForMs;
      this.expiresAt =
        this.grant?.id === grant.id
          ? Math.min(this.expiresAt, expires)
          : expires;
      this.grant = {
        id: grant.id,
        epoch: grant.epoch,
        policyVersion: grant.policyVersion,
      };
      this.sequence = Math.max(this.sequence, authority.ackSeq ?? 0);
    } else {
      this.disarm();
      this.grant = null;
    }
    if (!authority?.fresh) this.confirmed = false;
    if (
      this.sent &&
      this.awaitedSequence > 0 &&
      authority.verifiedSeq >= this.awaitedSequence &&
      this.names.every((name) => this.current?.[name] === this.sent[name])
    )
      this.confirmed = true;
    this.inputs.speed.max = String(authority?.limits?.speedCap ?? 0);
    this.inputs.depth.max = String(
      Math.max(1, authority?.limits?.maximumDepth ?? 1),
    );
    this.constrainStroke();
    this.render();
  }
  async tick() {
    if (this.active && !this.canSend()) this.requestStop();
    if (
      !this.active ||
      !this.canSend() ||
      this.inFlight ||
      (!this.pending && !this.sent)
    )
      return;
    if (this.sequence >= Number.MAX_SAFE_INTEGER) {
      this.requestStop();
      this.showError("Start a new session before sending more changes.");
      return;
    }
    const desired = { ...(this.pending ?? this.sent) };
    const sequence = ++this.sequence;
    if (this.userSend) {
      this.sent = desired;
      this.pending = null;
      this.confirmed = false;
      this.awaitedSequence = sequence;
      this.userSend = false;
    }
    this.inFlight = true;
    try {
      // Renew only the last explicitly sent tuple. Unsent slider edits never enter the run lease.
      const reply = await this.reference.invokeMethodAsync("SubmitRun", {
        v: 2,
        epoch: this.grant.epoch,
        policyVersion: this.grant.policyVersion,
        grantId: this.grant.id,
        seq: sequence,
        run: true,
        desired,
      });
      if (
        !reply?.ok &&
        reply?.code !== "policy-invalid" &&
        reply?.code !== "rate-limited"
      ) {
        this.showError(
          "Settings were rejected. Check the limits and try again.",
        );
        this.requestStop();
      }
    } catch {
      this.disarm();
      this.ready = false;
      this.showError("Connection lost.");
    } finally {
      this.inFlight = false;
      this.flushStop();
      this.render();
    }
  }
  async flushStop() {
    if (!this.pendingStop || this.inFlight || this.destroyed) return;
    this.pendingStop = false;
    this.inFlight = true;
    try {
      const reply = await this.reference.invokeMethodAsync("StopRun");
      if (!reply?.ok)
        this.showError(
          "Stop was not acknowledged. The connection watchdog remains active.",
        );
    } catch {
      this.ready = false;
      this.showError("Connection lost. The watchdog will stop control.");
    } finally {
      this.inFlight = false;
      this.stopping = false;
      this.render();
    }
  }
  marker(name, kind, value) {
    const marker = this.root.querySelector(`[data-${kind}-marker="${name}"]`);
    marker.toggleAttribute("hidden", !Number.isFinite(value));
    if (!Number.isFinite(value)) return;
    const input = this.inputs[name];
    const min = Number(input.min),
      max = Number(input.max);
    const y =
      100 -
      Math.max(0, Math.min(1, (value - min) / Math.max(1, max - min))) * 100;
    marker.setAttribute("y1", y);
    marker.setAttribute("y2", y);
  }
  render() {
    if (this.destroyed) return;
    const selected = this.selected();
    for (const name of this.names) {
      const current = this.current?.[name];
      const sent = this.sent?.[name];
      this.marker(name, "current", current);
      this.marker(name, "sent", sent);
      this.inputs[name].setAttribute(
        "aria-valuetext",
        `Selected value: ${selected[name]}. Sent value: ${sent ?? "none"}. Current value: ${current ?? "unavailable"}.`,
      );
    }
    this.sendButton.disabled = !this.canSend();
    this.autoSendToggle.disabled = !this.canSend();
    const status = this.active
      ? this.confirmed
        ? "Last sent settings verified. Stop ends control."
        : "Sent. Waiting for device feedback."
      : "Choose settings, then Send.";
    const display = {
      selected,
      sent: this.sent,
      confirmed: this.confirmed,
      status,
      error: this.error,
    };
    const serialized = JSON.stringify(display);
    if (serialized !== this.lastDisplayPayload) {
      this.lastDisplayPayload = serialized;
      this.pendingDisplay = display;
      void this.flushDisplay();
    }
  }
  async flushDisplay() {
    if (this.displayInFlight || this.destroyed) return;
    this.displayInFlight = true;
    try {
      while (this.pendingDisplay && !this.destroyed) {
        const display = this.pendingDisplay;
        this.pendingDisplay = null;
        await this.reference.invokeMethodAsync("UpdateDisplay", display);
      }
    } catch {
      this.disarm();
      this.ready = false;
      this.sendButton.disabled = true;
      this.autoSendToggle.disabled = true;
      console.error("Guest display connection failed; sending is disabled.");
    } finally {
      this.displayInFlight = false;
    }
  }
  showError(message) {
    this.error = message;
    this.render();
  }
  destroy() {
    if (this.destroyed) return;
    // A lost transport cannot carry Stop; admin revocation and its watchdog own stopping.
    if (disconnected) this.disarm();
    else this.requestStop();
    this.destroyed = true;
    clearInterval(this.interval);
    this.listeners.abort();
    this.observer.disconnect();
    this.pendingDisplay = null;
    this.sendButton.disabled = true;
    this.autoSendToggle.disabled = true;
  }
}
