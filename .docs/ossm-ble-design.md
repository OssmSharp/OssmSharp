# OSSM BLE integration design

Status: draft for discussion, 29 September 2026; no implementation approval.

## Purpose and evidence

Give .NET callers predictable connection, settings, Stop, and observation behavior
over the RAD BLE v1 interface discovered in OSSM-BLE-Web. The existing
[codemap](plan.md) targets RAD, while the current Bluetooth implementation still
uses direct command writes and state reads. This note separates source-confirmed
protocol behavior from proposed library policy; it does not replace the codemap.

Baseline: OSSM-BLE-Web commit `b79a8aab9b87956c9c6ec9234ef00a7fd65cc7f7`,
with its pinned RAD implementation references. This is source inspection, not
device testing or proof of compatibility with every firmware release.

## Caller scenarios

- **Connect or reconnect:** reuse an undisposed client, establish subscriptions and
  fresh state, then arm submission without sending or replaying motion settings.
- **Submit settings:** supply a complete pattern, speed, stroke, depth, and
  sensation request; distinguish completion, supersession, cancellation, and
  partial failure without claiming physical motion.
- **Observe or stop:** cached reads return last known state; live reads require a
  new observation. Unexpected changes and Stop disarm and discard pending settings.
- **Disconnect or dispose:** disconnect preserves client reuse; disposal is
  terminal. Both await issued I/O and cleanup. Failed setup remains disarmed.

These are library requirements, not guarantees supplied by the BLE protocol.

## Confirmed wire contract

The OSSM service UUID is `522b443a-4f53-534d-0001-420badbabe69`.
Characteristic identifiers replace its fourth UUID group:

| Group | Purpose |
| --- | --- |
| `0002` | Protocol Info: JSON identifying `rad-ble`, version `1`, and limits |
| `1000` / `1100` | JSON Request writes / Response indications |
| `2000` | Compact State: mode, sequence, uptime, active operation, lease metadata |
| `2010` | Essential State: summary including position and session counters |
| `2300` | Optional Sensor Stream: binary header followed by a payload |

Requests contain `v`, a nonzero `id`, `op`, and optional `path`, `args`,
`lease`, and `ifState`. Responses correlate by `id`; `accepted` is intermediate,
while `completed` and `failed` are terminal. Bluetooth write completion alone is
insufficient. The RAD baseline limits messages to 509 bytes.

Individual controls use `setting.write`, paths `motion.pattern`, `motion.speed`,
`motion.stroke`, `motion.depth`, and `motion.sensation`, and `args.value`.
`setting.read` returns individual values. The client's combined-settings helper
issues separate requests; it is not an atomic device transaction. Additional
operations include `target.set` for modes and `target.emergencyStop`.

`control.acquire` returns a numeric `lease` token and `ttlMs`; renewal extends
its lifetime and release relinquishes it. The firmware binds the token to its
Bluetooth connection and expiry. It is temporary exclusive control, not user
authentication. Writes and stream control require a valid lease.

Compact State does not contain the five motion controls. Motion observations
include those controls plus homing, position, buffer, targets, and counters.
`stream.start` on `motion.speed` selects the motion surface, not speed alone.
Only one stream is active; its frames have a 20-byte little-endian header carrying
version, stream ID, flags, format, payload length, sequence, uptime, and dropped
count. The web client's snapshot helper temporarily starts and stops that stream
and rejects calls while a separately started stream is active.

## Proposed integration

Use .NET 10 and Shiny Bluetooth on Windows and macOS, not a browser host or a
TypeScript port. Keep lease tokens, renewal, request IDs, and subscriptions hidden
from consumers. Await subscription readiness before issuing requests; resolve
write tasks only from matching terminal responses, preserving failure codes.

Maintain one motion stream per connection. Serve live reads from its next valid
observation instead of starting competing snapshot streams. Validate frame sizes,
version, stream identity, and required controls; never substitute defaults or
cached success for invalid live data.

Composition is proposed, not adopted: a concrete device coordinator could use an
`IDeviceAdapter` implemented separately for Bluetooth and the simulator. Shared
code owns arming, supersession, and last known state; adapters own protocol
translation and whole-request versus split-write execution. Split writes must
check cancellation before issuance, register expected changes, and await issued
I/O. The simulator process remains externally owned.

Retaining the current inheritance hooks is the smaller migration alternative;
both structures must enforce the same behavior. Reject a generic byte-transport
interface that merely moves protocol differences into casts or conditionals.
Do not copy client-side pattern policies or introduce legacy protocol fallback.

## Decisions and verification

- **Stop mapping:** the codemap specifies speed zero; the discovered client uses
  `target.set` with `target.emergencyStop`. Confirm firmware effects and agree the
  public Stop contract before changing it; neither response proves physical stop.
- **Readiness and feedback:** `setting.write` only stores values; patterns run only in the
  stroke engine, so each submission first sends `target.set` `target.strokeEngine` and
  requires `stateAfter` to be `strokeEngine.idle` or `strokeEngine.pattern`. Entering the
  engine resets stroke, depth, sensation, and speed; that reset becomes the expected
  baseline. Reported speed is the knob-scaled effective speed, so it is excluded from
  external-change detection. Knob configuration is never changed.
- **Recovery:** explicitly reconnect with fresh protocol state and reject stale
  callbacks. Lease loss fails the connection and disarms; never silently reacquire
  and replay settings. Cancellation does not prove that an issued write stopped.
- **Security:** the RAD baseline advertises open security; a lease is not identity
  verification. Exclude tokens and raw payloads from logs.
- **Planned checks:** test response correlation, partial writes, malformed frames,
  stream exclusivity, lease expiry, Stop races, and reconnect/disposal separately
  from host BLE integration. Hardware timing and physical readiness remain unverified.

## Sources

- [OSSM operations and paths](https://github.com/ReadieFur/OSSM-BLE-Web/blob/b79a8aab9b87956c9c6ec9234ef00a7fd65cc7f7/src/OssmBleClient.ts)
- [RAD requests, responses, and streams](https://github.com/ReadieFur/OSSM-BLE-Web/blob/b79a8aab9b87956c9c6ec9234ef00a7fd65cc7f7/src/RadBleApi.ts)
- [OSSM observation types](https://github.com/ReadieFur/OSSM-BLE-Web/blob/b79a8aab9b87956c9c6ec9234ef00a7fd65cc7f7/src/OssmProtocolSchema.ts)
- [RAD limits](https://github.com/researchanddesire/rad-ble/blob/e0aca3336eb67af2b6090c94e7b4f1896b09b47a/protocol/rad-ble-v1.json) and [lease enforcement](https://github.com/researchanddesire/rad-ble/blob/e0aca3336eb67af2b6090c94e7b4f1896b09b47a/src/RadBle.cpp)
