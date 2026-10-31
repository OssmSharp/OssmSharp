# RAD BLE integration notes

Draft dated 29 September 2026. This records the original proposal, not current
behavior or approval. Use the [runtime guide](plan.md) and source for current
behavior.

## Evidence

Baseline: OSSM-BLE-Web commit `b79a8aab9b87956c9c6ec9234ef00a7fd65cc7f7`
and its pinned RAD references. Source inspection does not establish hardware
compatibility or readiness.

## Wire contract

The service UUID is `522b443a-4f53-534d-0001-420badbabe69`.
Characteristics replace its fourth group:

| Group | Purpose |
| --- | --- |
| `0002` | Protocol Info: `rad-ble`, version `1`, and limits |
| `1000` / `1100` | JSON request writes / response indications |
| `2000` | Compact State: mode, sequence, uptime, active operation, lease |
| `2010` | Essential State: position and session counters |
| `2300` | Optional binary sensor stream |

Requests contain `v`, nonzero `id`, `op`, and optional `path`, `args`, `lease`,
and `ifState`. Responses match by `id`. `accepted` is intermediate;
`completed` and `failed` are terminal. Messages are limited to 509 bytes.

Controls use `setting.write` with `motion.pattern`, `motion.speed`,
`motion.stroke`, `motion.depth`, or `motion.sensation` and `args.value`.
`setting.read` reads one control. Combined settings require separate requests,
not an atomic transaction. Mode changes use `target.set`.

`control.acquire` returns a numeric lease and `ttlMs`. Renewal extends it;
release ends it. The lease belongs to the Bluetooth connection and expires.
It grants temporary exclusive control, not authentication.

Compact State lacks motion controls. `stream.start` on `motion.speed` selects
the full motion surface. Only one stream can run. Frames have a 20-byte
little-endian header with version, stream ID, flags, format, length, sequence,
uptime, and dropped count. The web snapshot helper briefly starts this stream
and cannot run alongside another stream.

## Proposed library rules

Use .NET 10 and Shiny on Windows and macOS. Hide leases, renewal, request IDs,
and subscriptions from callers. Wait for subscriptions before sending requests
and match terminal responses, preserving failure codes.

Keep one motion stream per connection. Live reads wait for its next valid
observation. Check frame size, version, stream identity, and required controls;
do not replace invalid data with defaults or cached success.

A coordinator plus Bluetooth adapter was proposed as an alternative to
inheritance. Either design must serialize writes, track pending changes,
check cancellation before sending, and await transfers already started.
Avoid generic byte transports, client-side pattern rules, and legacy fallback.

Reconnect explicitly with fresh state. Reject stale callbacks. Lease loss
ends the connection and disarms requests; never silently reacquire and replay.

## Decisions needing hardware checks

- **Stop:** compare speed zero with `target.set` on `target.emergencyStop`
  before changing the public contract.
- **Readiness:** `setting.write` stores controls. Pattern requests first select
  `target.strokeEngine` and require `strokeEngine.idle` or
  `strokeEngine.pattern`. Entry resets stroke, depth, sensation, and speed.
  The proposal sets `speedKnobAsLimit` to `false` before entry.
- **Feedback:** knob-scaled effective speed cannot identify external changes.
- **Security:** the baseline uses open security. Keep tokens and raw payloads
  out of logs.

Check response matching, partial writes, malformed frames, stream exclusivity,
lease expiry, Stop races, reconnect, and disposal separately from native BLE.
Hardware timing and physical readiness remain unverified.

## Sources

- [OSSM operations](https://github.com/ReadieFur/OSSM-BLE-Web/blob/b79a8aab9b87956c9c6ec9234ef00a7fd65cc7f7/src/OssmBleClient.ts)
- [RAD requests and streams](https://github.com/ReadieFur/OSSM-BLE-Web/blob/b79a8aab9b87956c9c6ec9234ef00a7fd65cc7f7/src/RadBleApi.ts)
- [Observation types](https://github.com/ReadieFur/OSSM-BLE-Web/blob/b79a8aab9b87956c9c6ec9234ef00a7fd65cc7f7/src/OssmProtocolSchema.ts)
- [RAD limits](https://github.com/researchanddesire/rad-ble/blob/e0aca3336eb67af2b6090c94e7b4f1896b09b47a/protocol/rad-ble-v1.json)
- [Lease enforcement](https://github.com/researchanddesire/rad-ble/blob/e0aca3336eb67af2b6090c94e7b4f1896b09b47a/src/RadBle.cpp)
