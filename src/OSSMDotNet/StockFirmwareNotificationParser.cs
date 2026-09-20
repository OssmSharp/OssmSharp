namespace OSSMDotNet;

internal static class StockFirmwareNotificationParser
{
    public static bool IsExpectedAcknowledgement(string payload, string expectedCommand) =>
        !string.IsNullOrEmpty(expectedCommand) &&
        string.Equals(payload, $"ok:{expectedCommand}", StringComparison.Ordinal);
}