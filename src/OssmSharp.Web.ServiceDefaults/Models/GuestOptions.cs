using System.ComponentModel.DataAnnotations;

namespace OssmSharp.Web.ServiceDefaults.Models;

/// <summary>
/// Configures the guest web listener.
/// </summary>
public sealed class GuestOptions
{
    /// <summary>
    /// Gets or sets the guest listener port, from 1024 through 65535.
    /// </summary>
    [Range(1024, 65535)]
    public int GuestPort { get; set; }
}
