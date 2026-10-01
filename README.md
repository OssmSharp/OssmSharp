

## Web Speed Control

The guest's simple-stroke speed slider is stroke-relative. The browser sends
`floor(selected speed * stroke / 100)` as the firmware velocity percentage.
For example, 50% selected speed with a 10% stroke sends 5%. Depth positions the
stroke; it is not an additional speed multiplier.

The firmware accepts integer percentages. Converted speeds below 1% send zero
and do not produce motion. The runtime, CLI and control API retain firmware
velocity-percent semantics. The guest preview uses the converted selection;
the user preview uses acknowledged transmissions. Both previews are illustrative,
not measurements of physical position or calibrated cycle time.

This follows the firmware's [stroke timing calculation](https://github.com/KinkyMakers/OSSM-hardware/blob/b7f01bf6df1be6f3ebf17dc0e31ed64ddf4c15b7/Software/lib/StrokeEngine/src/StrokeEngine.cpp#L830).
[OSSM-BLE-Web](https://github.com/ReadieFur/OSSM-BLE-Web/blob/main/src/OssmBleClient.ts)
passes speed percentages through unchanged; it does not provide stroke-relative conversion.

## Troubleshooting

**Error:**

```
/usr/local/share/dotnet/packs/Microsoft.macOS.Sdk.net10.0_26.0/26.0.11017/targets/Xamarin.Shared.Sdk.targets(2382,3): error This version of .NET for macOS (26.0.11017) requires Xcode 26.0. The current version of Xcode is 27.0. Either install Xcode 26.0, or use a different version of .NET for macOS. See https://aka.ms/xcode-requirement for more information.
```

Run:

```bash
dotnet workload install macos --version 10.0.401
```
