# Contributor rules

## Terms

Use these replacements:
- qualification -> readiness
- report -> last known state
- disabled -> disarmed, when referring to when the device detects unexpected motion or is stopped by the caller.
- stock -> oem, in code and internal technical prose.
- operator -> user
- submission outcome -> result
- submission -> request
- stroke-relative speed percentage -> speed (as an integer percentage)
- stroke relative -> <remove it.>
- whole-number firmware velocity -> raw velocity
- Stop cleanup -> stop

Validate only basic input ranges, such as 0%-100%, not pattern-specific rules.

Send patterns and settings. The firmware controls motion and decides whether it
is safe; the library does not control the motor directly.

Before changing tests, check whether `MockerTestBase<T>` supports the subject
under test. If so, inherit from and use it.

User-facing prose must not describe the device as OEM.

Write [README.md](README.md) for library developers: include package installation
and a working quick start. Keep [README.web.md](README.web.md) for nontechnical
device users, with only setup, usage, and troubleshooting.

Use only OssmSharp OEM runtime source and documentation. Exclude ossm-rs.

## Assumptions

- OssmDevice has exclusive control of a device while connected.
- There are no physical controls on the device.

## Speed Contract

Use speed percentages in public APIs, consumers, sliders, current markers, and previews.
Keep all conversions between speed and raw velocity inside `OssmSharp.Runtime.Oem`.
Do not convert in consumers.

- Document units, ranges, stroke dependence, zero stroke, precision, and rounding.
- Validate requested speed and stroke in `[0, 100]`; `50` means 50%, not `0.5`.
- Calculate current speed from velocity and stroke in the same snapshot, never pending settings.
- Preserve device decimals and calculated speeds above 100%.
- Exclude depth and sensation from conversions.
- Test commands, state, and displays at zero and rounding boundaries.

These percentages do not measure physical speed or strokes per minute.
All values below are percentages.

| Variable | Meaning | Limits |
| --- | --- | --- |
| `speedPercent` | Requested speed (as an integer percentage) | `[0, 100]` |
| `strokePercent` | Requested stroke | `[0, 100]` |
| `desiredDeviceVelocityPercent` | Desired velocity to be sent to the device | Whole number in `[0, 100]` |
| `currentDeviceVelocityPercent` | Velocity received from the device (not normalised with the stroke) | Preserve decimal values |
| `currentDeviceStrokePercent` | Stroke received from the device | `[0, 100]` |
| `displayedSpeedPercent` | Speed calculated from device state | Preserve decimal values and values above 100%; unknown at zero device stroke |

Required formulas (`floor` rounds down):

```text
desiredDeviceVelocityPercent = floor(speedPercent * strokePercent / 100)
displayedSpeedPercent = 100 * currentDeviceVelocityPercent / currentDeviceStrokePercent
```

Send zero raw velocity when speed or stroke is zero, or the result is below 1%.
Show unknown speed at zero device stroke; never substitute requested speed.
Do not round or cap current speed.

At 10% stroke, 53% requested speed sends 5% raw velocity and gives 50% current speed.

Discuss whether observed values confirm applied settings only in `OssmDevice.SetAsync`.

## Logging

In `OssmSharp.Runtime.Oem.OssmDevice`, log connection changes and API calls at
Information, commands and steps at Debug. Keep logs useful and brief.

## CI

Diagnose CI failures from run logs and artifacts. Never imitate a runner with
altered `PATH` entries, stub executables, or symlinks.
