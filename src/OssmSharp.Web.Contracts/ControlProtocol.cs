namespace OssmSharp.Web.Contracts;

/// <summary>
/// Defines shared identifier validation for the versioned control-service contract.
/// </summary>
public static class ControlProtocol
{
    /// <summary>
    /// Checks that text is nonempty and within a protocol field's length bound.
    /// </summary>
    /// <param name="value">The text to validate.</param>
    /// <param name="maximum">The maximum permitted character count.</param>
    /// <returns>Whether the text meets the bound.</returns>
    public static bool ValidText(string? value, int maximum) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximum;
}
