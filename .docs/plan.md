# Runtime and CLI guide

The CLI and web API use `OssmSharp.Runtime.Oem`. It depends on
`OssmSharp.Runtime` for discovery and result contracts, and Shiny for Bluetooth.
The firmware runs patterns and owns motion safety.

See [architecture](../docs/ARCHITECTURE.md) for diagrams and
[Directory.Packages.props](../Directory.Packages.props) for package versions.

## Runtime

| Type | Role |
| --- | --- |
| `IDeviceDiscovery` / `DeviceDiscovery` | Scan for devices advertising the fixed OSSM service without connecting |
| `DiscoveredDevice` | Carry the platform identifier and advertised name |
| `IOssmDevice` / `OssmDevice` | Connect, send settings, read state, stop, and release resources |
| `MotionSettings` | Check the pattern enum and percentage ranges; produce commands ending with speed |
| `DeviceState` | Parse complete observations and calculate current speed |
| `SetResult` / `SetStatus` | Distinguish completed, superseded, and cancelled settings requests |
| `DeviceWriteException` | Carry the failed request and known write progress |
| `OssmDeviceOptions` | Configure request attempts and response timeouts |
| `Request` | Encode RAD requests without interpreting pattern arguments |

`AddOssmSharpOemRuntime()` registers discovery, validated options, and
`Func<string, IOssmDevice>`. Native Bluetooth requires Windows or macOS;
portable hosts must register an `IBleManager`. Callers own and asynchronously
dispose devices created by the factory.

### Connection and requests

Connecting reads fresh state and arms requests without replaying settings.
Disconnecting stops and releases the connection but permits reuse. Disposal is
final. Connection failures disarm requests and remain in `ConnectionFailure`
until a successful reconnect.

`SetAsync` enters or reuses pattern mode, then sends a complete set of controls.
A newer accepted request replaces older commands not yet sent. Preparation or
homing can reject a request without disarming.

`EmergencyStopAsync` drops pending settings and sends zero speed before queued
work. Success preserves arming; failure disarms. `ResetAsync` rearms without
restoring settings. `ResetPositionAsync` instead retracts the rail and stops.

`GetStateAsync()` returns the last known state; `bypassCache: true` waits for
the next valid observation. Read errors propagate instead of returning cached
success. `StateChanged` announces state and arming changes; observations alone
do not change arming.

### Protocol

`OssmDevice` owns RAD request IDs, response matching, lease renewal, and one
motion stream per connection. Requests complete on matching terminal responses,
not Bluetooth transfer completion. Lost lease ownership ends the connection;
the library does not silently reacquire control or replay settings.

Stream parsing rejects malformed frames and missing or invalid controls.
Speed conversion stays inside the runtime; see the
[speed contract](../AGENTS.md#speed-contract).

## CLI

The CLI uses System.CommandLine for launch arguments, PrettyPrompt for input
and history, and Microsoft.Extensions.Hosting for service lifetimes.

```sh
OssmSharp.Cli list
OssmSharp.Cli prompt
OssmSharp.Cli prompt --device <identifier>
```

`list` prints identifiers and names without connecting. `prompt` connects
automatically only when exactly one device is found. With several devices,
list them and select one explicitly. Discovery or connection failures end the
process before opening the prompt.

| Command | Behavior |
| --- | --- |
| `set <pattern> <speed> <stroke> <depth> <sensation>` | Send all five controls |
| `stop` | Drop pending settings and stop |
| `reset` | Rearm without restoring settings |
| `status` | Read fresh state and display current speed |
| `help [command]` | Show commands, syntax, patterns, and ranges |
| `quit` | Stop, wait for cleanup, and exit |

Pattern accepts a defined ID from 0 to 6 or a case-insensitive name. Other
arguments are integer percentages from 0 to 100. Decimal and exponent forms
are rejected before anything is sent.

Commands run one at a time. Ctrl+C cancels pending work and issues Stop;
otherwise it begins normal exit. History lives in the user's application-data
directory; recalled commands must be sent explicitly.

Results and diagnostics redraw the prompt without losing partial input.
Only result keywords are colored. Redirected streams, `NO_COLOR`, and
`TERM=dumb` suppress result colors. Results, help, and listings go to stdout;
prompts, progress, and errors go to stderr.

Input errors keep the prompt open. A lost connection ends the session after
cleanup; start the CLI again to reconnect. Exit codes are `0` for normal exit,
`1` for discovery, selection, connection, or terminal I/O failures, and `2`
for invalid launch arguments.

## Deferred ideas

- **Observation-based disarming:** distinguish external changes from delayed
  feedback and mode-entry resets before using observations to disarm.
- **RAD adapter:** extract Bluetooth lifecycle, requests, leases, and streams
  only if it simplifies `OssmDevice`.
- **JSON parsing:** consider generated metadata instead of manual checks,
  retaining strict protocol, version, stage, and duplicate-field validation.
