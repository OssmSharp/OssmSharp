using System.Globalization;

namespace OssmSharp.Cli.Commands;

internal static class DeviceText
{
    internal static string Sanitize(string value) =>
        string.Concat(value.Select(static character =>
            char.GetUnicodeCategory(character) is UnicodeCategory.Control
                or UnicodeCategory.Format
                or UnicodeCategory.LineSeparator
                or UnicodeCategory.ParagraphSeparator
                ? ' '
                : character));
}
