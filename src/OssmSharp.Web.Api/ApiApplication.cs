namespace OssmSharp.Web.Api;

/// <summary>
/// Configures the control API and its native Bluetooth services.
/// </summary>
public static class ApiApplication
{
    /// <summary>
    /// Builds the control API with its existing authentication and control routes.
    /// </summary>
    /// <param name="builder">The configured application builder.</param>
    /// <returns>The application, which the caller must stop and dispose.</returns>
#if MACOS
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Reliability",
        "CA2000",
        Justification = "The resolved singleton factory transfers queue ownership to the application; pre-build failures dispose it explicitly.")]
#endif
    public static WebApplication Build(WebApplicationBuilder builder)
    {
        builder.AddServiceDefaults();
#if MACOS
        var queue = new CoreFoundation.DispatchQueue("OssmSharp.Web.Api.Bluetooth");
#endif
        WebApplication? app = null;
        try
        {
#if MACOS
            builder.Services.AddSingleton(_ => queue);
            Shiny.BleServiceCollectionExtensions.AddBluetoothLE(
                builder.Services,
                new Shiny.BluetoothLE.AppleBleConfiguration(DispatchQueue: queue));
#endif
            builder.Services.AddApiServices().AddSignalR();
            builder.Services.AddProblemDetails();
            app = builder.Build();
#if MACOS
            // Resolve first so the application owns the queue until its Bluetooth services are disposed.
            app.Services.GetRequiredService<CoreFoundation.DispatchQueue>();
#endif
            app.UseExceptionHandler();
            app.UseWebSockets();
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapDefaultEndpoints();
            app.MapControlHubV2();
            return app;
        }
        catch
        {
            if (app is not null)
                ((IDisposable)app).Dispose();
#if MACOS
            else
                queue.Dispose();
#endif
            throw;
        }
    }
}
