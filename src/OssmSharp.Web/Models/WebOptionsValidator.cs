using Microsoft.Extensions.Options;

namespace OssmSharp.Web.Models;

/// <summary>
/// Validates host listener settings without reflection in native builds.
/// </summary>
[OptionsValidator]
public sealed partial class WebOptionsValidator : IValidateOptions<WebOptions>
{
}
