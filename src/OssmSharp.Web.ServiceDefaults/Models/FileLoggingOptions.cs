using System.ComponentModel.DataAnnotations;

namespace OssmSharp.Web.ServiceDefaults.Models;

/// <summary>
/// Configures structured file logging for a web application.
/// </summary>
public sealed record FileLoggingOptions
{
    /// <summary>
    /// Gets or sets the directory for daily, size-limited log files.
    /// </summary>
    [Required]
    public string Directory { get; set; } = string.Empty;
}
