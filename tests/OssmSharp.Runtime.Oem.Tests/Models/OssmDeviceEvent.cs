namespace OssmSharp.Runtime.Oem.Tests.Models;

internal sealed record OssmDeviceEvent(OssmDeviceEventKind Kind, int Speed, int Stroke, int HeldIndex);
