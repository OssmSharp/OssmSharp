using Microsoft.Extensions.Hosting;

namespace OssmSharp.Web;

/// <summary>
/// Leaves process signals to the outer host while preserving each application's lifetime.
/// </summary>
public sealed class ApplicationHostLifetime : IHostLifetime
{
    /// <inheritdoc />
    public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
