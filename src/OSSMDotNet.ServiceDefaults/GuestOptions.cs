using System.ComponentModel.DataAnnotations;

namespace OSSMDotNet.ServiceDefaults;

/// <summary>Configures the guest web listener.</summary>
public sealed class GuestOptions
{
    [Range(1024, 65535)]
    public int GuestPort { get; set; }
}
