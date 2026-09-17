import { initialize } from "./_content/SharedUi/connection-ui.js";

const connection = initialize();
try {
    await Blazor.start({ circuit: connection.circuit });
    connection.ready();
} catch {
    connection.failed();
}
