const disconnectedKey = "ossm-guest-connection-lost";
let invitation = captureInvitation();
let controller = null;
let guestRoot = null;
let disconnected = sessionStorage.getItem(disconnectedKey) === "true";
const disconnectObserver = globalThis.MutationObserver ? new MutationObserver(() => disconnected && showDisconnect()) : null;

document.addEventListener(
  "click",
  (event) => {
    const link = event.composedPath().find((element) => element.tagName === "A");
    if (link?.origin === location.origin && link.pathname === "/join" && link.hash) event.stopImmediatePropagation();
  },
  { capture: true },
);

for (const eventName of ["popstate", "hashchange"]) {
  window.addEventListener(
    eventName,
    (event) => {
      if (location.pathname !== "/join" || !location.hash) return;
      event.stopImmediatePropagation();
      location.reload();
    },
    { capture: true },
  );
}

function captureInvitation() {
  const fragment = location.hash;
  history.replaceState(null, "", location.pathname);
  if (!fragment || fragment.length > 512 || location.pathname !== "/join") return null;
  const values = new URLSearchParams(fragment.slice(1));
  const secret = values.get("invite");
  const valid = [...values.keys()].length === 1 && secret?.length <= 128 && secret.trim();
  if (valid) sessionStorage.removeItem(disconnectedKey);
  return valid ? secret : null;
}

/** Consumes the scrubbed invitation once; subsequent calls return null. */
export function takeInvitation() {
  const value = invitation;
  invitation = null;
  return value;
}
/** Attaches local controls to the current guest component, replacing old listeners. */
export function attach(root, reference) {
  controller?.destroy();
  guestRoot = root;
  controller = new Controls(root, reference);
  if (disconnected) {
    showDisconnect();
    controller.destroy();
  }
}
/** Updates last known device values and permission without replacing local selections. */
export function updateState(state) {
  controller?.updateState(state);
}
/** Clears waiting selections and requested speed, and invalidates older replies without sending a device command. */
export function disarm() {
  controller?.disarm();
  controller?.render();
}
/** Releases local listeners and pending display work; connection-loss persistence remains. */
export function dispose() {
  invitation = null;
  controller?.destroy();
  controller = null;
  guestRoot = null;
}
/** Locks controls after transport loss until the user opens a new invitation. */
export function disconnect() {
  disconnected = true;
  sessionStorage.setItem(disconnectedKey, "true");
  showDisconnect();
  dispose();
}
/** Restores the persistent connection-loss notice after markup replacement. */
export function restoreDisconnected() {
  if (disconnected) showDisconnect();
}

function showDisconnect() {
  disconnectObserver?.disconnect();
  document.documentElement.dataset.guestConnectionLost = "true";
  guestRoot?.setAttribute("data-connection-lost", "true");
  document.querySelector(".preview-rail")?.pauseAnimations();
  for (const control of document.querySelectorAll?.(".guest-session button, .guest-session input, .guest-session select") ?? []) control.disabled = true;
  document.querySelector("main > [data-testid='guest-disconnected']")?.removeAttribute("hidden");
  disconnectObserver?.observe(document.body, { subtree: true, childList: true, attributes: true, attributeFilter: ["disabled", "hidden"] });
}

class Controls {
  constructor(root, reference) {
    this.root = root;
    this.reference = reference;
    this.listeners = new AbortController();
    this.names = ["speed", "stroke", "depth", "sensation"];
    this.inputs = Object.fromEntries(this.names.map((name) => [name, root.querySelector(`[data-input="${name}"]`)]));
    this.patternButtons = Array.from(root.querySelectorAll("[data-pattern]"));
    this.pattern = Number(this.patternButtons.find((button) => button.getAttribute("aria-pressed") === "true").dataset.pattern);
    this.sendButton = root.querySelector("[data-send-button]");
    this.resetPositionButton = root.querySelector("[data-reset-position-button]");
    this.autoSendToggle = root.querySelector("[data-auto-send]");
    this.error = null;
    this.errorVariant = "danger";
    this.lastDisplayPayload = null;
    this.ready = false;
    this.joined = false;
    this.destroyed = false;
    this.sentSelection = null;
    this.current = null;
    this.stopQueued = false;
    this.resetQueued = false;
    this.resettingPosition = false;
    this.previewStopped = false;
    this.recoveryRequired = false;
    this.commandGeneration = 0;
    this.pendingSelection = null;
    this.activeSelection = null;
    this.sending = false;
    this.pendingDisplay = null;
    this.updatingDisplay = false;
    this.on(this.sendButton, "click", () => this.commitSelection());
    this.on(root.querySelector("[data-stop-button]"), "click", () => this.requestStop());
    this.on(this.resetPositionButton, "click", () => this.requestResetPosition());
    this.on(this.autoSendToggle, "change", () => {
      this.render();
      if (this.autoSendToggle.checked) this.commitSelection();
    });
    for (const button of this.patternButtons)
      this.on(button, "click", () => {
        if (button.disabled) return;
        this.pattern = Number(button.dataset.pattern);
        this.render();
        if (this.autoSendToggle.checked) this.commitSelection();
      });
    for (const input of Object.values(this.inputs))
      this.on(input, "input", () => {
        this.constrainStroke();
        this.render();
        if (this.autoSendToggle.checked) this.commitSelection();
      });
    this.on(window, "pagehide", () => this.requestStop());
    this.constrainStroke();
    this.render();
  }

  on(target, event, action) {
    target.addEventListener(event, action, { signal: this.listeners.signal });
  }
  canSend() {
    return !this.destroyed && !this.recoveryRequired && !this.stopQueued && !this.resetQueued && !this.resettingPosition && this.joined && this.ready && document.visibilityState === "visible";
  }
  constrainStroke() {
    const depth = Math.max(0, Math.min(100, Number(this.inputs.depth.value)));
    this.inputs.depth.value = String(depth);
    this.inputs.stroke.max = String(depth);
    this.inputs.stroke.value = String(Math.max(0, Math.min(depth, Number(this.inputs.stroke.value))));
    this.inputs.speed.value = String(Math.max(0, Math.min(100, Number(this.inputs.speed.value))));
    this.inputs.sensation.value = String(Math.max(0, Math.min(100, Number(this.inputs.sensation.value))));
  }
  selected() {
    return {
      pattern: this.pattern,
      ...Object.fromEntries(this.names.map((name) => [name, Number(this.inputs[name].value)])),
    };
  }
  commitSelection() {
    if (!this.canSend()) return;
    const settings = this.selected();
    if (!this.changed(settings)) return;
    this.pendingSelection = settings;
    this.render();
    this.sendPending();
  }
  async sendPending() {
    if (this.sending) return;
    this.sending = true;
    try {
      while (this.pendingSelection && this.canSend()) {
        const settings = this.pendingSelection;
        this.pendingSelection = null;
        if (this.matches(settings, this.sentSelection)) continue;
        const generation = this.commandGeneration;
        this.activeSelection = settings;
        this.render();
        const reply = await this.reference.invokeMethodAsync("SubmitSettings", settings);
        this.activeSelection = null;
        if (this.destroyed || generation !== this.commandGeneration) continue;
        if (!reply?.ok) this.sentSelection = null;
        if (reply?.code === "outcome-unknown") {
          this.error = "Settings response timed out. Send your latest settings again.";
          this.errorVariant = "warning";
        } else if (reply?.ok) {
          this.sentSelection = settings;
          this.previewStopped = false;
          this.error = null;
        } else if (reply?.code === "Superseded") {
          this.error = null;
        } else {
          this.settingsFailed(reply?.code);
        }
        this.render();
      }
    } catch {
      this.connectionLost();
    } finally {
      this.activeSelection = null;
      this.sending = false;
      this.render();
    }
  }
  changed(settings) {
    return !this.matches(settings, this.pendingSelection ?? this.activeSelection ?? this.sentSelection);
  }
  matches(settings, other) {
    return other && Object.keys(settings).every((key) => settings[key] === other[key]);
  }
  settingsFailed(code) {
    this.errorVariant = "danger";
    if (code === "invalid-input" || code === "invalid-settings") {
      this.error = "Settings were rejected. Check the values and try again.";
    } else if (code === "not-ready" || code === "device-not-ready") {
      this.error = "The device is not ready. Wait for it to finish preparing, then send again.";
      this.errorVariant = "warning";
    } else if (code === "control-not-allowed") {
      this.error = "Control is not allowed. Wait for the other person to allow it.";
      this.errorVariant = "warning";
    } else if (code === "Cancelled" || code === "cancelled") {
      this.error = "Settings request cancelled. Send your latest settings again.";
      this.errorVariant = "warning";
    } else if (code === "device-control-lost" || code === "device-not-connected") {
      this.error = "Device connection lost. Ask the other person to reconnect it.";
      this.ready = false;
      this.disarm();
    } else {
      this.error = "Unable to send settings. Check the device connection and try again.";
    }
  }
  disarm() {
    this.commandGeneration++;
    this.pauseSelection();
    this.inputs.speed.value = "0";
    this.inputs.speed.style.setProperty("--range-progress", "0%");
    this.root.querySelector('[data-value="speed"]').textContent = "0";
  }
  pauseSelection() {
    this.autoSendToggle.checked = false;
    this.previewStopped = true;
    this.sentSelection = null;
    this.pendingSelection = null;
  }
  requestResetPosition() {
    if (!this.canSend()) return;
    this.pauseSelection();
    const generation = ++this.commandGeneration;
    this.resetQueued = true;
    this.render();
    this.reference
      .invokeMethodAsync("ResetPosition")
      .then((reply) => {
        if (this.destroyed || generation !== this.commandGeneration) return;
        this.error = reply?.ok ? null : `Reset Position failed (${reply?.code ?? "unknown"}). Use Stop before sending again.`;
        this.errorVariant = "danger";
        this.recoveryRequired = !reply?.ok;
      })
      .catch(() => this.connectionLost())
      .finally(() => {
        this.resetQueued = false;
        this.render();
      });
  }
  requestStop() {
    this.disarm();
    if (!this.joined || this.destroyed || this.stopQueued) return;
    this.stopQueued = true;
    this.recoveryRequired = true;
    this.render();
    this.reference
      .invokeMethodAsync("StopRun")
      .then((reply) => {
        if (this.destroyed) return;
        if (reply?.ok) {
          this.recoveryRequired = false;
          this.error = null;
        } else {
          this.error = "Stop was not acknowledged. Motion may still be active. Use Stop again before sending settings.";
          this.errorVariant = "danger";
        }
      })
      .catch(() => this.connectionLost())
      .finally(() => {
        this.stopQueued = false;
        this.render();
      });
  }
  updateState(state) {
    if (this.destroyed) return;
    const wasReady = this.ready;
    this.joined = state?.joined === true;
    this.ready = state?.ready === true;
    this.current = state?.current ?? null;
    this.resettingPosition = state?.resettingPosition === true;
    if ((this.resettingPosition || state?.stopped === true) && !this.previewStopped) {
      this.pauseSelection();
      this.commandGeneration++;
    }
    if (!this.joined || !this.ready) {
      this.disarm();
    } else if (!wasReady && state?.stopped !== true && !this.resettingPosition) {
      this.previewStopped = false;
    }
    this.render();
  }
  marker(name, kind, value) {
    const marker = this.root.querySelector(`[data-${kind}-marker="${name}"]`);
    marker.toggleAttribute("hidden", !Number.isFinite(value));
    if (!Number.isFinite(value)) return;
    const y = 100 - this.rangePercent(name, value);
    marker.setAttribute("data-outside-range", value > Number(this.inputs[name].max) ? "above" : value < Number(this.inputs[name].min) ? "below" : "");
    marker.setAttribute("y1", y);
    marker.setAttribute("y2", y);
  }
  rangePercent(name, value) {
    const input = this.inputs[name];
    return Math.max(0, Math.min(100, 100 * (value - Number(input.min)) / Math.max(1, Number(input.max) - Number(input.min))));
  }
  render() {
    if (this.destroyed) return;
    const selected = this.selected();
    for (const name of this.names) {
      const current = this.current?.[name];
      const sent = this.sentSelection?.[name];
      this.marker(name, "current", current);
      this.marker(name, "sent", sent);
      this.inputs[name].style.setProperty("--range-progress", `${this.rangePercent(name, selected[name])}%`);
      this.root.querySelector(`[data-value="${name}"]`).textContent = selected[name];
      this.root.querySelector(`[data-sent="${name}"]`).textContent = sent ?? "--";
      this.root.querySelector(`[data-current="${name}"]`).textContent = current ?? "--";
      this.inputs[name].setAttribute(
        "aria-valuetext",
        `Selected value: ${selected[name]}. Sent value: ${sent ?? "none"}. Current value: ${current ?? "unavailable"}.`,
      );
    }
    const canSend = this.canSend() && !this.autoSendToggle.checked && this.changed(selected);
    this.sendButton.disabled = !canSend;
    this.autoSendToggle.disabled = !this.canSend();
    this.resetPositionButton.disabled = !this.canSend();
    const adjustable = this.joined && this.ready && !this.resetQueued && !this.resettingPosition;
    for (const button of this.patternButtons) {
      button.disabled = !adjustable;
      button.setAttribute("aria-pressed", String(Number(button.dataset.pattern) === selected.pattern));
    }
    for (const input of Object.values(this.inputs)) input.disabled = !adjustable;
    this.root.toggleAttribute("data-sensation", selected.pattern !== 0);
    const status = this.resetQueued || this.resettingPosition ? "Resetting position ..." : this.activeSelection ? "Sending ..." : this.sentSelection ? "Settings sent." : "";
    const display = {
      selected,
      sent: this.sentSelection,
      status,
      error: this.error,
      errorVariant: this.errorVariant,
      canSend,
      previewStopped: this.previewStopped,
    };
    const serialized = JSON.stringify(display);
    if (serialized !== this.lastDisplayPayload) {
      this.lastDisplayPayload = serialized;
      this.pendingDisplay = display;
      this.updateDisplay();
    }
  }
  async updateDisplay() {
    if (this.updatingDisplay) return;
    this.updatingDisplay = true;
    try {
      while (!this.destroyed && this.pendingDisplay) {
        const display = this.pendingDisplay;
        this.pendingDisplay = null;
        await this.reference.invokeMethodAsync("UpdateDisplay", display);
      }
    } catch {
      this.connectionLost();
    } finally {
      this.updatingDisplay = false;
    }
  }
  connectionLost() {
    if (!this.destroyed) disconnect();
  }
  destroy() {
    if (this.destroyed) return;
    this.disarm();
    this.destroyed = true;
    this.pendingDisplay = null;
    this.listeners.abort();
    this.sendButton.disabled = true;
    this.resetPositionButton.disabled = true;
    this.autoSendToggle.disabled = true;
  }
}
