using System.ComponentModel.DataAnnotations;

namespace OSSMDotNet.ServiceDefaults;

/// <summary>Configures the local admin process and its restricted guest transport.</summary>
public sealed class AdminOptions : IValidatableObject
{
    [Range(1024, 65535)]
    public int AdminPort { get; set; }

    [Required]
    public string GuestOrigin { get; set; } = "";

    [Required, RegularExpression(@"(?i)\A(?!anonymous\z)[a-z0-9_-]{1,100}\z")]
    public string PipeName { get; set; } = "";

    [Required, RegularExpression(@"\A[A-Za-z0-9_-]{43}\z")]
    public string GuestServiceToken { get; set; } = "";

    [Required]
    public string SimulatorPath { get; set; } = "";

    /// <summary>Validates the guest origin and ensures it remains separate from the admin listener.</summary>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!Uri.TryCreate(GuestOrigin, UriKind.Absolute, out var origin) ||
            origin.Scheme != "http" || origin.Host != "127.0.0.1" ||
            origin.Port is < 1024 or > 65535 || origin.Port == AdminPort ||
            origin.UserInfo.Length != 0 || origin.AbsolutePath != "/" ||
            origin.Query.Length != 0 || origin.Fragment.Length != 0)
            yield return new("GuestOrigin must be a separate loopback HTTP origin without a path, credentials, query or fragment.", [nameof(GuestOrigin)]);
    }
}
