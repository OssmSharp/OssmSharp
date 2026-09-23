namespace OSSMDotNet.GuestWeb;

/// <summary>
/// Starts the restricted guest web application.
/// </summary>
public static class Program
{
    /// <summary>
    /// Builds and runs the guest application.
    /// </summary>
    public static async Task Main(string[] args)
    {
        await using var app = GuestApplication.Build(WebApplication.CreateBuilder(args));
        await app.RunAsync();
    }
}
