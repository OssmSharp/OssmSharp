using System.ComponentModel.DataAnnotations;

namespace OSSMDotNet.ServiceDefaults;

/// <summary>Configures the loopback-only admin web listener.</summary>
public sealed class AdminOptions
{
    [Range(1024, 65535)]
    public int AdminPort { get; set; }
}
