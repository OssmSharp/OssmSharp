namespace OssmSharp.Device.Oem;

/// <summary>
/// Identifies an advertised OEM device for listing and explicit connection selection.
/// </summary>
/// <param name="Identifier">The opaque platform identifier supplied by Shiny.</param>
/// <param name="Name">The advertised name, or null when the device supplies none.</param>
public sealed record DiscoveredDevice(string Identifier, string? Name);
