OssmSharp.Device.Primitives

IOssmDevice.cs
Define command submission, cancellation, Stop, reset, state retrieval, and state-change notifications through one device interface.
MotionSettings.cs
Carry the pattern identifier and control arguments without interpreting pattern behavior.
Validate basic control ranges only, such as percentages between zero and 100.
DeviceState.cs
Represent the last known device state without inferring motion safety or imposing expiry.
OssmSharp.Device.Runtime

DeviceBase.cs
SetAsync: Validate basic ranges and submit the pattern identifier and arguments without deciding motor behavior or safety.
ExecuteAsync: Run surviving commands in submission order, allowing only one device operation at a time.
Supersede: Cancel older pending commands and unissued portions of active commands, waiting for issued I/O before proceeding.
CancelPending: Cancel unissued work without claiming that already-issued I/O was recalled.
EmergencyStopAsync: Cancel pending commands, wait for issued I/O, send Stop, and disable further commands until explicit caller reset.
ResetAsync: Clear the disabled state explicitly without restarting previous motion.
GetState: Return the last known state without requiring another device update.
UpdateState: Store incoming state and notify subscribers.
DetectExternalChange: Disable command submission when changed controls cannot be explained by the current command, requiring explicit caller reset.
DisposeAsync: Cancel pending work and release owned resources after issued I/O settles.
OssmSharp.Device.Stock

StockOssmDevice.cs
ConnectAsync: Open the selected Bluetooth connection and receive device state updates.
DispatchAsync: Translate pattern identifiers and arguments into firmware commands without calibration, geometry, pattern-policy, or connection-matching checks.
DispatchStopAsync: Send the firmware Stop command independently of other control values.
ReadState: Decode firmware state updates and pass them to the shared runtime.
DisconnectAsync: Close the owned Bluetooth connection and release subscriptions.
Request.cs
Encode: Encode control names and values into the firmware’s command format.
StockBleServiceCollectionExtensions.cs
Register: Register the Stock transport and a factory returning DeviceBase.
OssmSharp.Device.Simulator

SimulatedDevice.cs
ConnectAsync: Connect to the supplied simulator endpoint without starting or owning its process.
DispatchAsync: Send pattern identifiers and arguments through the simulator protocol without duplicating its motion policy.
DispatchStopAsync: Send the simulator Stop command independently of other control values.
ReadState: Decode simulator state updates and pass them to the shared runtime.
DisconnectAsync: Close the connection without terminating the simulator process.
ServiceCollectionExtensions.cs
Register: Register the simulator implementation and its endpoint configuration.
OssmSharp.Cli

Program.cs
Main: Select the device implementation, configure dependencies, and run the command prompt.
PromptHandler.cs
RunAsync: Read commands while remaining responsive to newer commands, cancellation, and Stop.
Set: Parse pattern identifiers and control arguments, then submit them through IOssmDevice.
Cancel: Cancel pending device commands.
Stop: Request emergency Stop and display the disabled state.
Reset: Explicitly reset the device without resubmitting previous motion.
Status: Display the last known state and whether command submission is disabled.
Help: Describe available commands and basic argument ranges.
Quit: End command intake and await owned operations and cleanup.
