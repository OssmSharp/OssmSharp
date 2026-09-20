# OSSM-RS headless simulation bridge

`ossm-sim-bridge` is the external runtime used by the current standalone
`OSSMDotNet.Simulator` .NET library. It is a bounded, native JSON-lines adapter
for the **existing OSSM-RS motion engine**. The executable is built separately
and supplied to the .NET library at runtime; it is for a local controller
prototype, not hardware operation or stock-firmware qualification.

It has no GUI, network endpoint, BLE, serial port, device I/O, homing procedure,
or saved-state persistence. Its motor implementation is a no-op. Position and
velocity come from the upstream `DebugOut` callbacks, not a replacement motion
model. Each process starts connected and stopped:

```json
{ "pattern": 0, "speed": 0, "stroke": 10, "depth": 20, "sensation": 50 }
```

## Source provenance

- Repository: `https://github.com/ossm-rs/ossm-rs`.
- Target source commit: `578161b2e78d8ca70f8909f0ec34b8ae6014cfda`.
- Upstream license: Apache-2.0.
- Profile: `ossm-rs-sim-v1`.
- The lockfile includes `rsruckig` 2.1.3 at
  `d73e32f072aed81da3dba9b83b410062d27d9c6f`.

The manifest and lockfile pin the upstream Git revision. Cargo fetches that
revision when it is not already cached. The adapter does not read, replace,
build, or run firmware binaries.

The integration follows the local simulator's `src/motion_control.rs`:
`MotionControl::new_with_debug`, a dummy `Motor`, an Embassy-backed `Timer`, and
`update_handler` scheduled every 10 ms. The upstream `motion::run_motion` task
runs on the same current-thread Tokio runtime. Percentage controls are applied
synchronously through `motion::motion_state`; observations read those same
controls. No control update is split by an async yield.

## Build and run

From the repository root, with an existing Rust toolchain:

```shell
cargo test --locked --manifest-path tools/ossm-sim-bridge/Cargo.toml
cargo build --locked --release --manifest-path tools/ossm-sim-bridge/Cargo.toml
```

Cargo resolves `ossm-motion` from the pinned upstream Git revision and verifies
the checked-in lockfile. These commands do not install a toolchain or start a
GUI. Use Cargo's standard `--offline` option only after the registry and Git
revision are cached.

Executables:

```text
tools/ossm-sim-bridge/target/release/ossm-sim-bridge
tools/ossm-sim-bridge/target/release/ossm-sim-bridge.exe
```

The process takes no arguments. A minimal pipeline:

```shell
printf '%s\n' '{"v":1,"id":1,"op":"status"}' '{"v":1,"id":2,"op":"shutdown"}' \
  | tools/ossm-sim-bridge/target/release/ossm-sim-bridge
```

A controller should keep stdin open and continuously drain stdout. Replies can
arrive out of request order; correlate them by `id`, not by line position.

## Protocol v1

UTF-8, one JSON **object** per line; LF and CRLF are accepted. The maximum JSON
frame is 4096 bytes, excluding the line ending. A final nonempty frame at EOF is
processed. Unknown, duplicate, missing, null, wrongly typed, or
operation-inappropriate fields are rejected. JSON arrays are not substitutes for
request or desired objects.

All requests require `v: 1`, a positive `u64` integer `id`, and `op`.
`commandId`, when required, is also a positive `u64` integer. Optional fields
must be omitted when not applicable; explicit null is invalid.

| `op`         | Additional fields       | Effect                                                                               |
| ------------ | ----------------------- | ------------------------------------------------------------------------------------ |
| `status`     | None                    | Return the current published snapshot.                                               |
| `write`      | `commandId`, `desired`  | Apply desired controls; enable iff speed is nonzero.                                 |
| `stop`       | `commandId`             | Set speed to zero and disable; preserve the other current desired fields.            |
| `external`   | `desired`               | Inject controls into the same actual engine state; clear `commandId`.                |
| `delay`      | `value`: integer 0–5000 | Set latency in milliseconds for the next accepted write or stop.                     |
| `stale`      | `value`: integer 0 or 1 | Unfreeze or freeze published samples, respectively.                                  |
| `disconnect` | None                    | Mark disconnected, zero speed, disable, clear `commandId`, invalidate pending I/O.   |
| `connect`    | None                    | Mark connected, restore the stopped startup controls, clear `commandId`.             |
| `shutdown`   | None                    | Disable, zero speed, reply, and exit without waiting for pending artificial latency. |

`desired` has exactly these integer fields:

```json
{ "pattern": 0, "speed": 30, "stroke": 20, "depth": 40, "sensation": 50 }
```

Only pattern 0 is supported. Percentages must be 0–100, stroke must not exceed
depth, and nonzero speed requires positive stroke. Ordinary writes require
sensation 50. External injection permits sensation 0–100; the native Simple
Stroke pattern does not use sensation to change its trajectory.

Ordinary writes and stops require a connection. External injection is an
out-of-band fault control and remains available while disconnected; it does not
change the connection flag and can simulate externally enabled motion. A
subsequent disconnect or connect disables it again.

### Replies and events

Success reply:

```json
{
  "v": 1,
  "id": 1,
  "ok": true,
  "error": null,
  "state": {
    "sampleSequence": 2,
    "changeRevision": 0,
    "commandId": null,
    "desired": {
      "pattern": 0,
      "speed": 0,
      "stroke": 10,
      "depth": 20,
      "sensation": 50
    },
    "enabled": false,
    "moving": false,
    "position": 0.0,
    "velocity": 0.0,
    "connected": true,
    "profileId": "ossm-rs-sim-v1"
  }
}
```

Event, scheduled every 50 ms while not stale:

```json
{
  "v": 1,
  "event": "state",
  "state": {
    "sampleSequence": 3,
    "changeRevision": 0,
    "commandId": null,
    "desired": {
      "pattern": 0,
      "speed": 0,
      "stroke": 10,
      "depth": 20,
      "sensation": 50
    },
    "enabled": false,
    "moving": false,
    "position": 0.0,
    "velocity": 0.0,
    "connected": true,
    "profileId": "ossm-rs-sim-v1"
  }
}
```

`state` contains exactly the fields shown. `position` is simulated millimetres;
`velocity` is simulated millimetres/second. Both must be finite or the process
fails rather than publishing invalid values.

- `sampleSequence` advances for each fresh sample, including replies. It does
  not advance for repeated stale payloads. An initial sample is seeded before
  accepting requests, so the first visible sequence need not be 1.
- `changeRevision` advances only when desired controls, enabled state, or
  connection state actually change. Motion evolution, sampling, fault settings,
  and a new command ID on otherwise unchanged controls do not increment it.
- `commandId` is the last applied own write/stop ID, or null after external
  injection, connect, or disconnect. It is correlation metadata, not an
  idempotency/replay cache.
- `moving` is the real upstream
  `ossm_motion::motion_control::is_move_in_progress()` result, not an inference
  from speed or enabled state.

Validation failures omit `state`. Valid requests rejected as busy or
disconnected include the current published sample. Error codes are fixed and
never echo supplied content:

| Code                  | Meaning                                                                                            |
| --------------------- | -------------------------------------------------------------------------------------------------- |
| `invalid_request`     | Invalid JSON/UTF-8, shape, field type, ID, operation, or payload.                                  |
| `unsupported_version` | A structurally valid request has a version other than 1.                                           |
| `invalid_desired`     | Unsupported pattern, percentage/relationship violation, or ordinary-write sensation other than 50. |
| `frame_too_large`     | More than 4096 JSON bytes.                                                                         |
| `busy`                | Another ordinary write/stop already owns the pending slot.                                         |
| `disconnected`        | Ordinary command lacks a connection, or disconnect invalidated its pending completion.             |

Replies preserve a valid recoverable request ID. Malformed JSON, invalid or
duplicate IDs, non-object requests, and oversized frames use the reserved error
ID `0`, since those inputs have no trustworthy request identity:

```json
{ "v": 1, "id": 0, "ok": false, "error": "frame_too_large" }
```

### Delayed I/O and stale observations

There is at most one pending ordinary write or stop. Competing writes **and
stops** return `busy`; status, external changes, and fault injection remain
live. A new `delay` value affects the next accepted command, not an existing
deadline. Rejected commands do not consume that setting.

At expiry the originally issued write is applied, even if an external update
happened meanwhile. A delayed stop preserves the desired fields current at
completion, except speed. **A controller that abandons a delayed write must
handle its eventual completion and then issue a stop**; abandoning its local
wait does not cancel the simulated I/O.

A disconnect invalidates pending I/O even if connect occurs before its deadline.
At the original deadline it replies `disconnected` without mutation. The pending
slot remains occupied until then. EOF and shutdown end the process instead and
discard pending completions.

Stale mode freezes the entire last published sample, including its sequence,
revision, command ID, connection flag, and kinematics. It suppresses periodic
events; replies reuse that exact sample. Engine updates, actual control
mutations, and delayed completions continue. `stale: 0` returns a new actual
sample. Connect does not silently clear separately injected stale/delay faults.

## Native retract semantics and limits

**This preserves OSSM-RS native retract-on-disable behavior. It is not a stock
firmware stop guarantee.** Upstream `config::RETRACT_ON_MOTION_DISABLED` is
true; `motion::run_motion` retracts to `MIN_MOVE_MM` (10 mm) after observing an
enabled-to-disabled transition. A successful stop can therefore report speed 0,
`enabled: false`, and `moving: true` together. There is no instantaneous
standstill guarantee.

The adapter neither fabricates a home position nor forces the last debug
velocity to zero. Upstream's initial debug position is 0.0 before a first move,
despite the nominal 10 mm minimum. Debug callbacks can retain the last
trajectory values when a move finishes. These are simulation outputs, not
encoder measurements or mechanical safety evidence.

Scheduling is best-effort, not real time: nominal 10 ms engine updates and 50 ms
events are subject to Windows timer resolution and process scheduling. Missed
event periods do not produce a catch-up flood. Artificial latency is a minimum
deadline, not a hard real-time completion bound.

## Resource bounds and lifecycle

- Input: 4096-byte frames, an eight-item channel, and at most eight requests
  handled per engine tick. Oversized lines are drained with bounded storage,
  rejected once at their boundary, and do not prevent later valid frames.
- Output: one stdout writer, a 32-frame bounded channel, and one complete JSON
  line per write/flush. Output saturation fails the process instead of blocking
  the engine indefinitely. Closing stdout also fails it.
- Shutdown/EOF disable the engine, abort and await the tracked native motion
  task, and allow up to two seconds for queued output to drain. An unresponsive
  output pipe produces a nonzero exit, not a false successful shutdown.
- The blocking stdin OS thread is process-lifetime. It is not a Tokio blocking
  task, so shutdown exits even when the parent keeps stdin open.
- Stderr uses fixed diagnostics: at most four upstream error notices, one panic
  notice, and a terminal failure notice. No request or raw upstream error text
  is echoed. Upstream recoverable error logs retain upstream continuation
  behavior; panics, task exit, non-finite observations, and transport failures
  fail the adapter.
- Exit codes: 0 for shutdown/EOF, 1 for runtime/engine/transport failure, 2 for
  command-line arguments. Rejected JSON requests alone do not end the process.

Unit tests exercise parsing, bounds, serialization, scheduling, and pure state
transitions without constructing a global motion engine. Subprocess smoke checks
exercise the real core separately. No hardware, stock-firmware compatibility,
GUI behavior, or .NET controller integration is qualified here.
