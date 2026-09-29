Use these terms instead:
- qualification -> readiness
- report -> last known state
- disabled -> disarmed, when referring to when the device detects unexpected motion or is stopped by the caller.
- stock -> oem, when referring to the real device.


**Pattern-specific policy does not belong in the device library.**
The device library is to do basic checks on inputs only (e.g. 0%-100%). It must not verify patterns' inputs.

The device firmware accepts patterns and arguments. The library will not directly control the motor's motion - the library tells the motor which pattern and settings to use and it will decide whether the motion is safe.

Tests classes whose SUT is supported by MockerTestBase<T> MUST extend and use it.

Do not include any referneces to OEM in user-facing prose.
