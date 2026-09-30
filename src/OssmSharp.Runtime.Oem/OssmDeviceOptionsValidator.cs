using Microsoft.Extensions.Options;

namespace OssmSharp.Runtime.Oem;

[OptionsValidator]
internal sealed partial class OssmDeviceOptionsValidator : IValidateOptions<OssmDeviceOptions>
{
}
