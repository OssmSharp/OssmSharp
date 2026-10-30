# OEM firmware 1.0.34 evidence

Date inspected: 23 September 2026

Historical source notes, not the current RAD protocol contract or hardware
readiness evidence. Use the [runtime guide](../../plan.md) for current behavior.

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

`getCurrentState()` serializes:

- unsigned `millis()` timestamp;
- state name;
- integer speed, stroke, sensation, depth and buffer;
- integer pattern ordinal;
- position in millimetres with two decimal places;
- session UUID, initially empty.

Use UTC receipt time rather than uptime for application timestamps. Position is
a stepper counter, not a physical travel measurement.

### BLE state notifications

The notification fingerprint includes state, speed, stroke, sensation, depth,
pattern, and session ID. Only changes to these fields update the cached state
and trigger notifications. Timestamp, position, and buffer changes alone do not.
There is no effective periodic heartbeat despite the elapsed-time check.

### Command queue

The BLE loop dequeues a command, invokes `ble_click`, and caches `ok:<command>`
in the state characteristic without a notification.

State has no public command ID or sample sequence. Do not invent either in tests.

### Speed and local controls

With knob limiting enabled, effective speed is
`speedKnob * requestedBleSpeed / 100`. State casts it to an integer and omits the
knob percentage.

The local encoder changes stroke, sensation, depth, or buffer. Depth changes
trigger notifications; buffer changes do not.

## Limits of this evidence

Notification latency, loaded stopping distance, host BLE behavior,
competing-client behavior, and physical travel need hardware checks. This
historical firmware has local controls; the library assumes exclusive control
and a device with no physical controls.
