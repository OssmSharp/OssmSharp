using System.ComponentModel.DataAnnotations;

namespace OSSMDotNet.Web.HostRuntime;

public sealed class GuestOptions
{
    [Range(1024, 65535)]
    public int GuestPort { get; set; }

    [Required, RegularExpression(@"(?i)\A(?!anonymous\z)[a-z0-9_-]{1,100}\z")]
    public string PipeName { get; set; } = "";

    [Required, RegularExpression(@"\A[A-Za-z0-9_-]{43}\z")]
    public string GuestServiceToken { get; set; } = "";
}
