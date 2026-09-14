namespace OSSMDotNet.Web.GuestWeb;

public static class Program
{
    public static async Task Main(string[] args)
    {
        await using var app = GuestApplication.Build(WebApplication.CreateBuilder(args));
        await app.RunAsync();
    }
}
