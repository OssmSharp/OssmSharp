## External dependencies

- **Platform:** .NET 10 — runtime and standard libraries.
- **Stock Bluetooth:** Shiny.BluetoothLE 5.7.2 — access Windows/macOS Bluetooth APIs.
- **Stock hardware:** Stock device and firmware — execute commands and own motor behavior and safety.
- **Simulator:** `ossm-rs` / `ossm-motion` and the simulator process — communicate with and test against the real simulator.
- **CLI parsing:** System.CommandLine 2.0.12 — parse commands and options, and generate help.
- **Hosting:** Microsoft.Extensions.Hosting 10.0.12 — manage executable composition and lifecycle.
- **Logging:** Microsoft.Extensions.Logging.Abstractions 10.0.12 — provide standard `ILogger<T>` diagnostics.
- **Test framework:** xUnit v3 — define and organize tests.
- **Test runner:** Microsoft.Testing.Platform v2 — discover and execute tests.
- **Test mocking:** FastMoq — construct test subjects with mocked dependencies.
- **Test assertions:** AwesomeAssertions — express readable assertions and failure messages.

## Project dependency order

- **`OssmSharp.Device.Primitives`**
- **`OssmSharp.Device.Runtime`**
  - `OssmSharp.Device.Primitives`
- **`OssmSharp.Device.Stock`**
  - `OssmSharp.Device.Primitives`
  - `OssmSharp.Device.Runtime`
- **`OssmSharp.Device.Simulator`**
  - `OssmSharp.Device.Primitives`
  - `OssmSharp.Device.Runtime`
- **`OssmSharp.Cli`**
  - `OssmSharp.Device.Primitives`
  - `OssmSharp.Device.Runtime`
  - `OssmSharp.Device.Stock`
  - `OssmSharp.Device.Simulator`

## Projects, files, and functions

- **`OssmSharp.Device.Primitives`**
  - **`IOssmDevice.cs`**
    - **ConnectAsync:** Connect to the selected device and subscribe to its state updates.
    - **SetAsync:** Submit a pattern identifier and control arguments through the device implementation.
    - **CancelPending:** Cancel unissued commands without claiming that issued I/O was recalled.
    - **GetState:** Return the last known device state.
    - **EmergencyStopAsync:** Stop the device and disable further commands until explicit caller reset.
    - **ResetAsync:** Clear the disabled state without resubmitting previous motion.
    - **IsDisabled (property):** Indicate whether command submission is disabled pending caller reset.
    - **StateChanged (event):** Notify subscribers when the last known state or disabled status changes.
    - **DisposeAsync:** Await owned operations and release the device connection and subscriptions.
  - **`MotionSettings.cs`**
    - Carry the pattern identifier and control arguments without interpreting pattern behavior.
    - Validate basic control ranges only, such as percentages between zero and 100.
  - **`DeviceState.cs`**
    - Represent the last known device state without inferring motion safety or imposing expiry.

- **`OssmSharp.Device.Runtime`**
  - **`DeviceBase.cs`**
    - **SetAsync:** Validate basic ranges and submit the pattern identifier and arguments without deciding motor behavior or safety.
    - **ExecuteAsync:** Run surviving commands in submission order, allowing only one device operation at a time.
    - **Supersede:** Cancel older pending commands and unissued portions of active commands, waiting for issued I/O before proceeding.
    - **CancelPending:** Cancel unissued work without claiming that already-issued I/O was recalled.
    - **EmergencyStopAsync:** Cancel pending commands, wait for issued I/O, send Stop, and disable further commands until explicit caller reset.
    - **ResetAsync:** Clear the disabled state explicitly without restarting previous motion.
    - **GetState:** Return the last known state without requiring another device update.
    - **UpdateState:** Store incoming state and notify subscribers.
    - **DetectExternalChange:** Disable command submission when changed controls cannot be explained by the current command, requiring explicit caller reset.
    - **DisposeAsync:** Cancel pending work and release owned resources after issued I/O settles.

- **`OssmSharp.Device.Stock`**
  - **`StockOssmDevice.cs`**
    - **ConnectAsync:** Open the selected Bluetooth connection and receive device state updates.
    - **DispatchAsync:** Translate pattern identifiers and arguments into firmware commands without calibration, geometry, pattern-policy, or connection-matching checks.
    - **DispatchStopAsync:** Send the firmware Stop command independently of other control values.
    - **ReadState:** Decode firmware state updates and pass them to the shared runtime.
    - **DisconnectAsync:** Close the owned Bluetooth connection and release subscriptions.
  - **`Request.cs`**
    - **Encode:** Encode control names and values into the firmware’s command format.
  - **`StockBleServiceCollectionExtensions.cs`**
    - **Register:** Register the Stock transport and a factory returning DeviceBase.

- **`OssmSharp.Device.Simulator`**
  - **`SimulatedDevice.cs`**
    - **ConnectAsync:** Connect to the supplied simulator endpoint without starting or owning its process.
    - **DispatchAsync:** Send pattern identifiers and arguments through the simulator protocol without duplicating its motion policy.
    - **DispatchStopAsync:** Send the simulator Stop command independently of other control values.
    - **ReadState:** Decode simulator state updates and pass them to the shared runtime.
    - **DisconnectAsync:** Close the connection without terminating the simulator process.
  - **`ServiceCollectionExtensions.cs`**
    - **Register:** Register the simulator implementation and its endpoint configuration.

- **`OssmSharp.Cli`**
  - **`Program.cs`**
    - **Main:** Configure dependencies, create and connect the selected device, run the prompt through IOssmDevice, and own device disposal on exit or failure.
  - **`PromptHandler.cs`**
    - **RunAsync:** Read commands while remaining responsive to newer commands, cancellation, and Stop.
    - **Set:** Parse pattern identifiers and control arguments, then submit them through IOssmDevice.
    - **Cancel:** Cancel pending device commands.
    - **Stop:** Request emergency Stop and display the disabled state.
    - **Reset:** Explicitly reset the device without resubmitting previous motion.
    - **Status:** Display the last known state and whether command submission is disabled.
    - **Help:** Describe available commands and basic argument ranges.
    - **Quit:** End command intake and await owned operations and cleanup.

## Consumer call chains (examples)

- **Connect and start the prompt:**
  - **Input:** A selected Stock device.
  - **Chain:** `Program.Main` -> `StockBleServiceCollectionExtensions.Register` -> registered factory creates a `StockOssmDevice` returned as `DeviceBase` -> `IOssmDevice.ConnectAsync` (implemented by `StockOssmDevice.ConnectAsync`) -> Shiny Bluetooth connection.
  - **Outcome:** After connection succeeds, `Program.Main` passes the device as `IOssmDevice` to `PromptHandler.RunAsync`.
  - **Cleanup:** On prompt exit or connection failure, `Program.Main` -> `IOssmDevice.DisposeAsync` (implemented by `DeviceBase.DisposeAsync`) -> `StockOssmDevice.DisconnectAsync`.
  - **Simulator path:** Use `ServiceCollectionExtensions.Register`, `SimulatedDevice.ConnectAsync`, and `SimulatedDevice.DisconnectAsync` with the supplied endpoint; the adapter owns only its connection.

- **Submit settings:**
  - **Input:** A connected, enabled Stock device, a pattern identifier, and control arguments.
  - **Chain:** `PromptHandler.Set` -> `IOssmDevice.SetAsync` (implemented by `DeviceBase.SetAsync`) -> `DeviceBase.Supersede` -> `DeviceBase.ExecuteAsync` -> `StockOssmDevice.DispatchAsync` -> `Request.Encode` -> Shiny Bluetooth write -> device firmware.
  - **Execution:** Supersede older unissued work, wait for issued I/O, and send the remaining commands one at a time in order.
  - **Outcome:** Firmware receives the settings and determines motor behavior and safety; completing a write does not confirm physical motion.
  - **Asynchronous feedback:** Firmware state notification -> `StockOssmDevice.ReadState` -> `DeviceBase.UpdateState` -> state-change subscribers.
  - **Simulator path:** Replace Stock dispatch and Bluetooth with `SimulatedDevice.DispatchAsync` and the simulator protocol connection.

- **Read the last known state:**
  - **Input:** The consumer requests status.
  - **Chain:** `PromptHandler.Status` -> `IOssmDevice.GetState` (implemented by `DeviceBase.GetState`) and `IOssmDevice.IsDisabled` -> CLI display.
  - **Outcome:** Display the last known state and whether sending is disabled without issuing a device read or expiring stored state.

- **Stop and disable sending:**
  - **Input:** The consumer requests Stop.
  - **Chain:** `PromptHandler.Stop` -> `IOssmDevice.EmergencyStopAsync` (implemented by `DeviceBase.EmergencyStopAsync`) -> `DeviceBase.CancelPending` -> wait for issued I/O -> `StockOssmDevice.DispatchStopAsync` -> Shiny Bluetooth write -> firmware Stop command.
  - **Outcome:** Pending commands are cancelled and further settings remain blocked until explicit caller reset; cancellation does not recall issued I/O.
  - **Simulator path:** Replace Stock Stop dispatch and Bluetooth with `SimulatedDevice.DispatchStopAsync` and the simulator protocol connection.
