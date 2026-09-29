

## Troubleshooting

**Error:**

```
/usr/local/share/dotnet/packs/Microsoft.macOS.Sdk.net10.0_26.0/26.0.11017/targets/Xamarin.Shared.Sdk.targets(2382,3): error This version of .NET for macOS (26.0.11017) requires Xcode 26.0. The current version of Xcode is 27.0. Either install Xcode 26.0, or use a different version of .NET for macOS. See https://aka.ms/xcode-requirement for more information.
```

Run:

```bash
dotnet workload install macos --version 10.0.401
```
