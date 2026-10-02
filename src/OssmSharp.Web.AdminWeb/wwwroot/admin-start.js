import { initialize } from "./_content/OssmSharp.Web.Common/connection-ui.js";

const connection = initialize();
try {
    await Blazor.start({ circuit: connection.circuit });
} catch {
    connection.failed();
}
