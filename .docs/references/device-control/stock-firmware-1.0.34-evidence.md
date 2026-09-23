# Stock firmware 1.0.34 evidence snapshot

Date inspected: 23 September 2026

This local snapshot records decision-bearing behavior from the stock OSSM
firmware. It does not copy firmware source, replace the pinned upstream files,
or establish physical qualification. The authoritative device requirements
remain in [`spec.md`](../../specs/device-control/spec.md).

## Pinned source

- Firmware: `1.0.34`
- Commit: `4871a84c54ae0734b1f8bc3d02fcc0098f12fbad`
- Version header:
  <https://github.com/KinkyMakers/OSSM-hardware/blob/4871a84c54ae0734b1f8bc3d02fcc0098f12fbad/Software/src/constants/Version.h>
- State serialization:
  <https://github.com/KinkyMakers/OSSM-hardware/blob/4871a84c54ae0734b1f8bc3d02fcc0098f12fbad/Software/src/ossm/OSSM.cpp>
- BLE queue, cache and notification loop:
  <https://github.com/KinkyMakers/OSSM-hardware/blob/4871a84c54ae0734b1f8bc3d02fcc0098f12fbad/Software/src/services/communication/nimble.cpp>
- Local play controls and speed-knob scaling:
  <https://github.com/KinkyMakers/OSSM-hardware/blob/4871a84c54ae0734b1f8bc3d02fcc0098f12fbad/Software/src/ossm/play_controls/play_controls.cpp>

## Observed protocol behavior

### State payload

`getCurrentState()` serializes these fields:

- unsigned `millis()` timestamp;
- state name;
- integer speed, stroke, sensation, depth and buffer;
- integer pattern ordinal;
- position in millimetres with two decimal places;
- session UUID, initially empty.

The application must replace firmware uptime with the UTC receipt time. Position
is a stepper counter, not an independent measurement of physical travel.

### BLE state notifications

The notification fingerprint contains state, speed, stroke, sensation, depth,
pattern and session ID. It excludes timestamp, position and buffer. Therefore:

- depth, stroke, sensation, pattern, effective-speed and mode changes can produce
  notifications;
- timestamp or position changes alone do not produce notifications;
- buffer changes alone do not produce notifications;
- the loop updates its cached state value and notifies only when the fingerprint
  changes;
- there is no effective periodic state heartbeat, even though the loop checks
  elapsed time.

### Commands and applied state

The BLE loop dequeues a command, invokes `ble_click`, and stores `ok:<command>`
in the state characteristic. That acknowledgement is cached and is not notified.
It proves FIFO submission only; it does not prove that the resulting motion
settings were observed or applied.

The state payload has no public command ID or sample sequence. Tests must not
invent either field to attribute stock feedback to a write.

### Speed and local controls

The firmware stores BLE-requested speed separately. When knob limiting is
enabled, effective speed is `speedKnob * requestedBleSpeed / 100`, and telemetry
casts that value to an integer. The knob percentage itself is not in the state
payload.

The local encoder directly updates the active stroke, sensation, depth or buffer
setting. Depth changes are therefore represented in serialized state and in the
notification fingerprint. Buffer is serialized but omitted from the fingerprint.

## Limits of this evidence

This inspection does not prove notification latency, loaded stopping distance,
BLE behavior on a particular host, competing-client behavior, or physical
travel. Those remain physical qualification work under DC-01 through DC-09.
