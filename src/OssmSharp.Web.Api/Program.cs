namespace OssmSharp.Web.Api;

internal static class Program
{
    internal static Task Main(string[] args) => Task.Run(() => RunAsync(args));

    private static async Task RunAsync(string[] args)
    {
#if MACOS
        using var bluetoothQueue = new CoreFoundation.DispatchQueue("OssmSharp.Web.Api.Bluetooth");
#endif
        var builder = WebApplication.CreateBuilder(args);
        builder.AddServiceDefaults();
#if MACOS
        Shiny.BleServiceCollectionExtensions.AddBluetoothLE(
            builder.Services,
            new Shiny.BluetoothLE.AppleBleConfiguration(DispatchQueue: bluetoothQueue));
#endif
        builder.Services
            .AddApiServices()
            .AddSignalR();
        builder.Services.AddProblemDetails();

        await using var app = builder.Build();

        app.UseExceptionHandler();
        app.UseWebSockets();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapDefaultEndpoints();
        app.MapControlHubV2();
        await app.RunAsync().ConfigureAwait(false);
    }
}
