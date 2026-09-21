namespace OSSMDotNet;

internal static class StockFirmwareProfile
{
    private const string Prefix = "522b443a-4f53-534d-";
    private const string Suffix = "-420badbabe69";

    internal static readonly Guid Service = Create("0001");
    internal static readonly Guid Identity = Create("0010");
    internal static readonly Guid Command = Create("1000");
    internal static readonly Guid KnobConfiguration = Create("1010");
    internal static readonly Guid State = Create("2000");
    internal static readonly Guid Patterns = Create("3000");
    internal static readonly Guid PatternData = Create("3010");

    private static Guid Create(string value) => new($"{Prefix}{value}{Suffix}");
}
