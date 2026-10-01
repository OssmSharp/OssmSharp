using System.ComponentModel.DataAnnotations;

namespace OssmSharp.Web.ServiceDefaults.Models;

/// <summary>
/// Configures the loopback-only admin web listener.
/// </summary>
public sealed class AdminOptions
{
    /// <summary>
    /// Gets or sets the loopback listener port, from 1024 through 65535.
    /// </summary>
    [Range(1024, 65535)]
    public int AdminPort { get; set; }

    /// <summary>
    /// Gets or sets the required guest web origin used to create invitation links.
    /// </summary>
    [Required]
    [Url]
    public string GuestOrigin { get; set; } = string.Empty;
}
