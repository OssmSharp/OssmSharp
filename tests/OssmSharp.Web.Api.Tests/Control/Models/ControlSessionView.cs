using OssmSharp.Web.Contracts;

namespace OssmSharp.Web.Api.Tests.Control.Models;

internal sealed record ControlSessionView(bool Admitted, bool Attached, bool Allowed, bool FirstAttached, bool CanResume, bool Connected, DeviceSettingsSnapshot? Sent, string Reply);
