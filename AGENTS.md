Use these terms instead:
- qualification -> readiness
- report -> last known state
- disabled -> disarmed, when referring to when the device detects unexpected motion or is stopped by the caller.
- stock -> oem, in code and internal technical prose.
- operator -> user

**Pattern-specific policy does not belong in the device library.**

The device library is to do basic checks on inputs only (e.g. 0%-100%). It must not verify patterns' inputs.

The device firmware accepts patterns and arguments. The library will not directly control the motor's motion - the library tells the motor which pattern and settings to use and it will decide whether the motion is safe.

Before creating or changing tests, check whether MockerTestBase<T> supports the SUT. If it does, the test class must inherit from and use it.

User-facing prose must not describe the device as OEM.

Use only OssmSharp OEM runtime source and documentation; treat ossm-rs as out of scope.

Current project wiring does not establish intended service ownership. Each executable must have verified dependencies and standalone startup. AppHost composition and project references do not necessarily indicate required coupling.

## Speed Contract

Use stroke-relative speed percentages in every public API, consumer, slider, current marker, and preview.
Keep all conversions between stroke-relative speed and firmware speed inside `OssmSharp.Runtime.Oem`.
Consumers must not perform these conversions.

- Document units, ranges, stroke dependence, zero-stroke behavior, precision, and rounding in the library's public API.
- Validate requested speed and stroke within `[0, 100]`, using `50` for 50%, not `0.5`.
- Calculate current speed from firmware speed and stroke received in the same device snapshot, never from pending selections.
- Preserve decimal values from the device and in calculated current speed, including results above 100%.
- Do not include depth or sensation in speed conversions.
- Test commands, device state, and displays for zero values and rounding boundaries.

These percentages do not measure physical speed or strokes per minute.
All variables below are percentages, using `50` for 50%.

| Variable | Meaning | Limits |
| --- | --- | --- |
| `speedPercent` | Requested stroke-relative speed | `[0, 100]` |
| `strokePercent` | Requested stroke | `[0, 100]` |
| `desiredDeviceVelocityPercent` | Desired velocity to be sent to the device | Whole number in `[0, 100]` |
| `currentDeviceVelocityPercent` | Velocity received from the device (not normalised with the stroke) | Preserve decimal values |
| `currentDeviceStrokePercent` | Stroke received from the device | `[0, 100]` |
| `displayedSpeedPercent` | Stroke-relative speed calculated from device state | Preserve decimal values and values above 100%; unknown at zero device stroke |

Required formulas (`floor` rounds down to a whole number):

```text
desiredDeviceVelocityPercent = floor(speedPercent * strokePercent / 100)
displayedSpeedPercent = 100 * currentDeviceVelocityPercent / currentDeviceStrokePercent
```

Send zero firmware speed when requested speed or stroke is zero, or the calculated firmware speed is below 1%.
Show current speed as unknown when device stroke is zero; do not divide by zero or substitute the requested speed.
Do not round calculated current speed or limit it to 100%.

Rounding loses information: 53% requested speed at 10% stroke sends 5% firmware speed.
The same device values give 50% current speed, not 53%.

## Logging

In OssmSharp.Device.Oem OssmDevice and DeviceBase:
- Connection state changes and api calls must be logged and at the information level
- Commands and step logged at the debug level.
- Add logging proactively but not overwhelmingly.
