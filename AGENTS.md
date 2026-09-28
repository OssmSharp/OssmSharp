Use these terms instead:
- qualification -> readiness
- report -> last known state


**Pattern-specific policy does not belong in the device library.**
The device library is to do basic checks on inputs only (e.g. 0%-100%). It must not verify patterns' inputs.

The device firmware accepts patterns and arguments. The library will not directly control the motor's motion - the library tells the motor which pattern and settings to use and it will decide whether the motion is safe.
