namespace OSSMDotNet.Models;

/// <summary>Contains one guest motion request accepted only for its server-bound admission.</summary>
public sealed record GuestInputRequest(string Correlation, GuestInput Input);
