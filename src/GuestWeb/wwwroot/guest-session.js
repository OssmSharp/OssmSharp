let invitation = captureInvitation();
let controller = null;
let disconnected = false;

function captureInvitation() {
    const fragment = location.hash;
    history.replaceState(null, "", location.pathname);
    if (!fragment || fragment.length > 512 || !/^\/join\/[^/]+\/?$/.test(location.pathname)) return null;
    const values = new URLSearchParams(fragment.slice(1));
    const secret = values.get("invite");
    return [...values.keys()].length === 1 && secret?.length <= 128 && secret.trim() ? secret : null;
}

export function takeInvitation() {
    const value = invitation;
    invitation = null;
    return value;
}
export function attach(root, reference) {
    controller?.destroy();
    controller = new Controls(root, reference);
    if (disconnected) controller.destroy();
}
export function updateAuthority(authority) { controller?.updateAuthority(authority); }
export function disarm() { controller?.disarm(); }
export function dispose() { invitation = null; controller?.destroy(); controller = null; }
export function disconnect() { disconnected = true; dispose(); }

class Controls {
    constructor(root, reference) {
        this.root = root;
        this.reference = reference;
        this.listeners = new AbortController();
        this.names = ["speed", "stroke", "depth"];
        this.inputs = Object.fromEntries(this.names.map(name => [name, root.querySelector(`[data-input="${name}"]`)]));
        this.sendButton = root.querySelector("[data-send-button]");
        this.auto = root.querySelector("[data-auto-send]");
        this.status = root.querySelector("[data-send-status]");
        this.alert = root.querySelector("[data-browser-alert]");
        this.ready = false;
        this.joined = false;
        this.active = false;
        this.destroyed = false;
        this.inFlight = false;
        this.pendingStop = false;
        this.stopping = false;
        this.grant = null;
        this.expiresAt = 0;
        this.seq = 0;
        this.sent = null;
        this.pending = null;
        this.current = null;
        this.awaitingSeq = 0;
        this.confirmed = false;
        this.userSend = false;
        this.on(this.sendButton, "click", () => this.commitSelection());
        this.on(root.querySelector("[data-stop-button]"), "click", () => this.requestStop());
        this.on(this.auto, "change", () => {
            if (this.auto.checked) this.commitSelection();
            else {
                this.pending = null;
                this.userSend = false;
                if (!this.sent) this.active = false;
                this.render();
            }
        });
        for (const input of Object.values(this.inputs)) this.on(input, "input", () => {
            this.constrainStroke();
            if (this.auto.checked && this.canSend()) this.commitSelection();
            this.render();
        });
        this.on(window, "blur", () => this.requestStop());
        this.on(document, "visibilitychange", () => {
            if (document.visibilityState !== "visible") this.requestStop();
        });
        this.on(window, "pagehide", () => this.destroy());
        this.observer = new MutationObserver(() => { if (!root.isConnected) this.destroy(); });
        this.observer.observe(document.body, { childList: true, subtree: true });
        this.interval = setInterval(() => this.tick(), 100);
        this.constrainStroke();
        this.render();
    }

    on(target, event, action) { target.addEventListener(event, action, { signal: this.listeners.signal }); }
    canSend() {
        return !this.destroyed && this.joined && this.ready && this.grant &&
            performance.now() < this.expiresAt && document.visibilityState === "visible" && !this.pendingStop && !this.stopping;
    }
    constrainStroke() {
        const depth = Number(this.inputs.depth.value);
        this.inputs.stroke.max = String(depth);
        if (Number(this.inputs.stroke.value) > depth) this.inputs.stroke.value = String(depth);
    }
    selected() {
        return { pattern: 0, sensation: 50, ...Object.fromEntries(this.names.map(name => [name, Number(this.inputs[name].value)])) };
    }
    commitSelection() {
        if (!this.canSend()) return;
        this.pending = this.selected();
        this.userSend = true;
        this.active = true;
        this.alert.hidden = true;
        this.render();
    }
    disarm() {
        this.active = false;
        this.pending = null;
        this.userSend = false;
        this.auto.checked = false;
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
        if (grant && this.ready && this.joined && typeof grant.id === "string" && grant.id.length > 0 &&
            typeof grant.epoch === "string" && grant.epoch.length > 0 &&
            Number.isSafeInteger(grant.policyVersion) && grant.policyVersion === authority.policyVersion &&
            Number.isSafeInteger(grant.validForMs) && grant.validForMs > 0) {
            if (this.grant?.epoch !== grant.epoch) {
                this.disarm();
                this.sent = null;
                this.awaitingSeq = 0;
                this.confirmed = false;
                this.seq = 0;
            }
            const expires = performance.now() + grant.validForMs;
            this.expiresAt = this.grant?.id === grant.id ? Math.min(this.expiresAt, expires) : expires;
            this.grant = { id: grant.id, epoch: grant.epoch, policyVersion: grant.policyVersion };
            this.seq = Math.max(this.seq, authority.ackSeq ?? 0);
        } else {
            this.disarm();
            this.grant = null;
        }
        if (!authority?.fresh) this.confirmed = false;
        if (this.sent && this.awaitingSeq > 0 && authority.verifiedSeq >= this.awaitingSeq &&
            this.names.every(name => this.current?.[name] === this.sent[name])) this.confirmed = true;
        this.inputs.speed.max = String(authority?.limits?.speedCap ?? 0);
        this.inputs.depth.max = String(Math.max(1, authority?.limits?.maximumDepth ?? 1));
        this.constrainStroke();
        this.render();
    }
    async tick() {
        if (this.active && !this.canSend()) this.requestStop();
        if (!this.active || !this.canSend() || this.inFlight || (!this.pending && !this.sent)) return;
        if (this.seq >= Number.MAX_SAFE_INTEGER) {
            this.requestStop();
            this.showError("Start a new session before sending more changes.");
            return;
        }
        const desired = { ...(this.pending ?? this.sent) };
        const sequence = ++this.seq;
        if (this.userSend) {
            this.sent = desired;
            this.pending = null;
            this.confirmed = false;
            this.awaitingSeq = sequence;
            this.userSend = false;
        }
        this.inFlight = true;
        try {
            // Renew only the last explicitly sent tuple. Unsent slider edits never enter the run lease.
            const reply = await this.reference.invokeMethodAsync("SubmitRun", {
                v: 2, epoch: this.grant.epoch, policyVersion: this.grant.policyVersion,
                grantId: this.grant.id, seq: sequence, run: true, desired
            });
            if (!reply?.ok) {
                this.showError("Settings were rejected. Check the limits and try again.");
                this.requestStop();
            }
        } catch {
            this.disarm();
            this.ready = false;
            this.showError("Connection lost. Open a new invitation.");
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
            if (!reply?.ok) this.showError("Stop was not acknowledged. The connection watchdog remains active.");
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
        const min = Number(input.min), max = Number(input.max);
        const y = 100 - Math.max(0, Math.min(1, (value - min) / Math.max(1, max - min))) * 100;
        marker.setAttribute("y1", y);
        marker.setAttribute("y2", y);
    }
    render() {
        const selected = this.selected();
        for (const name of this.names) {
            const current = this.current?.[name];
            const sent = this.sent?.[name];
            this.root.querySelector(`[data-value="${name}"]`).textContent = selected[name];
            this.root.querySelector(`[data-sent="${name}"]`).textContent = sent ?? "--";
            this.marker(name, "current", current);
            this.marker(name, "sent", sent);
            const unsent = sent === undefined || selected[name] !== sent;
            const verified = !unsent && this.confirmed && current === sent;
            this.root.querySelector(`[data-slider="${name}"]`).dataset.state = verified ? "verified" : unsent ? "unsent" : "pending";
            this.inputs[name].setAttribute("aria-valuetext",
                `Selected ${selected[name]}, sent ${sent ?? "none"}, current ${current ?? "unavailable"}`);
        }
        this.sendButton.disabled = !this.canSend();
        this.auto.disabled = !this.canSend();
        this.status.textContent = !this.ready ? "Sending disabled by the operator."
            : this.active ? this.confirmed ? "Last sent settings verified. Stop ends control." : "Sent. Waiting for device feedback."
            : "Choose settings, then Send.";
    }
    showError(message) { this.alert.textContent = message; this.alert.hidden = false; }
    destroy() {
        if (this.destroyed) return;
        this.requestStop();
        this.destroyed = true;
        clearInterval(this.interval);
        this.listeners.abort();
        this.observer.disconnect();
    }
}
