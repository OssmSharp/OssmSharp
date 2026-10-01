Use these terms instead:
- qualification -> readiness
- report -> last known state
- disabled -> disarmed, when referring to when the device detects unexpected motion or is stopped by the caller.
- stock -> oem, in code and internal technical prose.


**Pattern-specific policy does not belong in the device library.**

The device library is to do basic checks on inputs only (e.g. 0%-100%). It must not verify patterns' inputs.

The device firmware accepts patterns and arguments. The library will not directly control the motor's motion - the library tells the motor which pattern and settings to use and it will decide whether the motion is safe.

Before creating or changing tests, check whether MockerTestBase<T> supports the SUT. If it does, the test class must inherit from and use it.

User-facing prose must not describe the device as OEM.

Use only OssmSharp OEM runtime source and documentation; treat ossm-rs as out of scope.

Current project wiring does not establish intended service ownership. Each executable must have verified dependencies and standalone startup. AppHost composition and project references do not necessarily indicate required coupling.
