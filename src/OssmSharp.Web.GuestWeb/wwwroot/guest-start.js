import { initialize } from "./_content/OssmSharp.Web.Common/connection-ui.js";
import { createSession } from "./guest-auth.js";

let guest;
const session = createSession((code) => guest?.showSessionState(code));
const connection = initialize(() => guest?.disconnect());

try {
  // Capture and scrub the fragment before Blazor sends its initial navigation URI to the circuit.
  const controller = document.getElementById("guest-start").dataset.controller;
  guest = await import(new URL(controller, document.baseURI).href);
  guest.configureSession(session, "opening");
  const code = await session.prepare(guest.takeInvitation());
  guest.showSessionState(code);
  if (code === "reload") {
    location.reload();
  } else {
    // Circuit loss disposes the API client; only a fresh cookie-authenticated circuit can recover it.
    await Blazor.start({
      circuit: {
        ...connection.circuit,
        reconnectionHandler: {
          onConnectionDown: () => guest.disconnect(),
          onConnectionUp: () => guest.restoreDisconnected(),
        },
      },
    });
    if (["connection-unavailable", "session-in-use", "stop-pending"].includes(code)) guest.disconnect(code);
    guest.restoreDisconnected();
  }
} catch {
  guest?.endSession("initialization-failed");
  connection.failed();
  reportError(new Error("Guest page initialization failed."));
}
