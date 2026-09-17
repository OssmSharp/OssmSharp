namespace OSSMDotNet.Models;

/// <summary>Defines shared identifier validation for the versioned control-service contract.</summary>
public static class ControlProtocol
{
    /// <summary>Returns whether text is nonempty and within the specified contract bound.</summary>
    public static bool ValidText(string? value, int maximum) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximum;
}
