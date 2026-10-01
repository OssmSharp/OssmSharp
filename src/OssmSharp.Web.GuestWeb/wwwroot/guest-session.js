const disconnectedKey = "ossm-guest-connection-lost";
let invitation = captureInvitation();
let controller = null;
let guestRoot = null;
let disconnected = sessionStorage.getItem(disconnectedKey) === "true";

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
export function updateState(state) {
  controller?.updateState(state);
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
  document.querySelector("main > [data-testid='guest-disconnected']")?.removeAttribute("hidden");
}

class Controls {
  constructor(root, reference) {
    this.root = root;
    this.reference = reference;
    this.listeners = new AbortController();
    this.names = ["speed", "stroke", "depth"];
    this.inputs = Object.fromEntries(this.names.map((name) => [name, root.querySelector(`[data-input="${name}"]`)]));
    this.sendButton = root.querySelector("[data-send-button]");
    this.autoSendToggle = root.querySelector("[data-auto-send]");
    this.error = null;
    this.lastDisplayPayload = null;
    this.ready = false;
    this.joined = false;
    this.destroyed = false;
    this.sent = null;
    this.sentSelection = null;
    this.current = null;
    this.stopQueued = false;
    this.recoveryRequired = false;
    this.commandGeneration = 0;
    this.commandQueue = Promise.resolve();
    this.displayQueue = Promise.resolve();
    this.on(this.sendButton, "click", () => this.commitSelection());
    this.on(root.querySelector("[data-stop-button]"), "click", () => this.requestStop());
    this.on(this.autoSendToggle, "change", () => this.autoSendToggle.checked && this.commitSelection());
    for (const input of Object.values(this.inputs))
      this.on(input, "input", () => {
        this.constrainStroke();
        this.render();
        if (this.autoSendToggle.checked) this.commitSelection();
      });
    this.on(window, "blur", () => this.requestStop());
    this.on(document, "visibilitychange", () => {
      if (document.visibilityState !== "visible") this.requestStop();
    });
    this.on(window, "pagehide", () => this.requestStop());
    this.constrainStroke();
    this.render();
  }

  on(target, event, action) {
    target.addEventListener(event, action, { signal: this.listeners.signal });
  }
  canSend() {
    return !this.destroyed && !this.recoveryRequired && !this.stopQueued && this.joined && this.ready && document.visibilityState === "visible";
  }
  constrainStroke() {
    const depth = Math.max(1, Math.min(100, Number(this.inputs.depth.value)));
    this.inputs.depth.value = String(depth);
    this.inputs.stroke.max = String(depth);
    this.inputs.stroke.value = String(Math.max(1, Math.min(depth, Number(this.inputs.stroke.value))));
    this.inputs.speed.value = String(Math.max(0, Math.min(100, Number(this.inputs.speed.value))));
  }
  selected() {
    return {
      pattern: 0,
      sensation: 50,
      ...Object.fromEntries(this.names.map((name) => [name, Number(this.inputs[name].value)])),
    };
  }
  commitSelection() {
    if (!this.canSend()) return;
    const settings = this.selected();
    if (!this.changed(settings)) return;
    const generation = this.commandGeneration;
    this.commandQueue = this.commandQueue
      .then(async () => {
        if (generation !== this.commandGeneration || !this.canSend() || !this.changed(settings)) return;
        const command = { ...settings, speed: Math.floor((settings.speed * settings.stroke) / 100) };
        const reply = await this.reference.invokeMethodAsync("SubmitSettings", command);
        if (this.destroyed) return;
        if (reply?.code === "outcome-unknown") {
          this.recoveryRequired = true;
          this.disarm();
          this.commandGeneration++;
          this.error = "Settings may have executed. The outcome is unknown. Use Stop before sending again.";
        } else if (generation !== this.commandGeneration) {
          return;
        } else if (reply?.ok) {
          this.sent = command;
          this.sentSelection = settings;
          this.error = null;
        } else {
          this.error = "Settings were rejected. Check the values and try again.";
        }
        this.render();
      })
      .catch(() => this.connectionLost());
  }
  changed(settings) {
    return !this.sentSelection || Object.keys(settings).some((key) => settings[key] !== this.sentSelection[key]);
  }
  disarm() {
    this.autoSendToggle.checked = false;
    this.sent = null;
    this.sentSelection = null;
  }
  requestStop() {
    this.disarm();
    this.commandGeneration++;
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
    this.joined = state?.joined === true;
    this.ready = state?.ready === true;
    this.current = state?.current ?? null;
    if (!this.joined || !this.ready) {
      this.disarm();
      this.commandGeneration++;
    }
    this.render();
  }
  marker(name, kind, value) {
    const marker = this.root.querySelector(`[data-${kind}-marker="${name}"]`);
    marker.toggleAttribute("hidden", !Number.isFinite(value));
    if (!Number.isFinite(value)) return;
    const input = this.inputs[name];
    const min = Number(input.min),
      max = Number(input.max);
    const y = 100 - Math.max(0, Math.min(1, (value - min) / Math.max(1, max - min))) * 100;
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
      this.root.querySelector(`[data-sent="${name}"]`).textContent = sent ?? "--";
      this.root.querySelector(`[data-current="${name}"]`).textContent = current ?? "--";
      this.inputs[name].setAttribute(
        "aria-valuetext",
        `Selected value: ${selected[name]}. Sent value: ${sent ?? "none"}. Last known value: ${current ?? "unavailable"}.`,
      );
    }
    this.sendButton.disabled = !this.canSend();
    this.autoSendToggle.disabled = !this.canSend();
    const status = this.sent ? "Last changed selection sent." : "Choose settings, then Send.";
    const display = {
      selected,
      sent: this.sent,
      status,
      error: this.error,
    };
    const serialized = JSON.stringify(display);
    if (serialized !== this.lastDisplayPayload) {
      this.lastDisplayPayload = serialized;
      this.displayQueue = this.displayQueue
        .then(() => !this.destroyed && this.reference.invokeMethodAsync("UpdateDisplay", display))
        .catch(() => this.connectionLost());
    }
  }
  connectionLost() {
    if (!this.destroyed) disconnect();
  }
  destroy() {
    if (this.destroyed) return;
    this.disarm();
    this.commandGeneration++;
    this.destroyed = true;
    this.listeners.abort();
    this.sendButton.disabled = true;
    this.autoSendToggle.disabled = true;
  }
}
