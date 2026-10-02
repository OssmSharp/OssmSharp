import { initialize } from "./_content/OssmSharp.Web.Common/connection-ui.js";

let guest;
const connection = initialize(() => guest?.disconnect());

try {
    // Capture and scrub the fragment before Blazor sends its initial navigation URI to the circuit.
    const controller = document.getElementById("guest-start").dataset.controller;
    guest = await import(new URL(controller, document.baseURI).href);
    await Blazor.start({ circuit: connection.circuit });
    guest.restoreDisconnected();
} catch {
    guest?.disconnect();
    connection.failed();
}
