# Stock firmware agent guide

Use this guide when changing device adapters, controller feedback handling or
tests. Read the [evidence snapshot](stock-firmware-1.0.34-evidence.md) and the
[authoritative requirements](../../specs/device-control/spec.md) before changing
behavior.

## Model in tests

- Target stock firmware `1.0.34` at commit `4871a84c54ae0734b1f8bc3d02fcc0098f12fbad`.
- Feed stock tests through `StateResponse.Parse` using the documented JSON shape
  and a caller-supplied UTC receipt time.
- Treat speed as effective, knob-scaled, integer telemetry. Do not equate it with
  the requested BLE speed.
- Treat stroke, sensation, depth and pattern as reported control settings.
- Treat position as changing feedback that must not trigger an override.
- Do not expect a notification for timestamp, position or buffer-only changes.
- Do not model periodic stock heartbeats.
- Do not add stock command IDs, sample sequences or causal attribution fields.
- Treat `ok:<command>` as submission acknowledgement, never applied-state proof.

## External-change tests

- P0: an unexplained timely effective-speed change publishes
  `UnexpectedMotionChangeException` and the controller locks.
- P2: an unexplained reported stroke, sensation, depth or pattern change uses
  the same exception and controller lock. One report produces at most one
  motion-change error, retaining the full prior, current and pending settings.
- Keep P0 and P2 assertions separate. P2 must not weaken or delay P0.
- Ignore ordinary timestamp and position changes when comparing control settings.
- Clear reported-state and pending-write baselines across disconnect/reconnect.
- A missing notification is an unconfirmed-feedback fault, not an external-change
  lock.
- A matching state establishes consistency, not exclusive causation.

## Simulator boundaries

The simulator may identify its own writes and injected changes directly. That is
not stock firmware behavior. Simulator tests may prove controller reactions, but
stock contract tests must use the rules above. Never populate the physical
qualified allowlist from simulator evidence.

## Claims agents must not make

Do not claim that these tests prove physical stopping, BLE latency, independent
interlock behavior, exact knob position, complete causal attribution or physical
travel. These require the qualification evidence listed in the device
specification.
