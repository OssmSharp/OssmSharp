using Microsoft.Extensions.Options;

namespace OssmSharp.Web.Api.Models;

[OptionsValidator]
internal sealed partial class ControlServiceOptionsValidator : IValidateOptions<ControlServiceOptions>;
