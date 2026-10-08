using Microsoft.Extensions.Hosting;

namespace OssmSharp.Web;

/// <summary>
/// Builds the independently configured applications owned by the web host.
/// </summary>
public interface IWebApplicationFactory
{
    /// <summary>
    /// Builds an application in startup order: API, guest, then user.
    /// </summary>
    /// <param name="index">The application index, from zero through two.</param>
    /// <returns>The application host. The caller owns its lifecycle and disposal.</returns>
    IHost Create(int index);
}
