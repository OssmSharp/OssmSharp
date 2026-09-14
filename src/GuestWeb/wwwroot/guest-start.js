const bootStatus = document.getElementById("guest-boot-status");
let guest;
let disconnected = false;

function connectionLost() {
    disconnected = true;
    guest?.disconnect();
    bootStatus.textContent = "Connection lost. Open a new invitation.";
    bootStatus.hidden = false;
}

try {
    // Capture and scrub the fragment before Blazor sends its initial navigation URI to the circuit.
    guest = await import("./guest-session.js");
    await Blazor.start({
        circuit: {
            reconnectionHandler: {
                onConnectionDown: connectionLost,
                onConnectionUp: () => {
                    if (!disconnected) {
                        bootStatus.hidden = true;
                    }
                }
            }
        }
    });
    if (!disconnected) {
        bootStatus.hidden = true;
    }
} catch {
    connectionLost();
    bootStatus.textContent = "Could not connect. Open a new invitation.";
}
