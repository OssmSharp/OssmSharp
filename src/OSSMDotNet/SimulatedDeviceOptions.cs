using System.ComponentModel.DataAnnotations;

namespace OSSMDotNet;

public sealed record SimulatedDeviceOptions(string executablePath) : IValidatableObject
{
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!File.Exists(executablePath))
        {
            yield return new ValidationResult("Build the ossm-sim-bridge executable before starting the prototype.");
        }
    }
}
