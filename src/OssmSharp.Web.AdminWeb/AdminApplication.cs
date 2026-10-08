namespace OssmSharp.Web.AdminWeb;

/// <summary>
/// Configures the local user web application.
/// </summary>
public static class AdminApplication
{
    /// <summary>
    /// Builds the user application with its loopback listener and control routes.
    /// </summary>
    /// <param name="builder">The configured application builder.</param>
    /// <param name="staticAssetsManifestPath">The endpoint manifest path, or null to use the application's default.</param>
    /// <returns>The application, which the caller must stop and dispose.</returns>
    public static WebApplication Build(WebApplicationBuilder builder, string? staticAssetsManifestPath = null)
    {
        builder.AddServiceDefaults();
        builder.Services.AddAdminConfiguration().AddLocalBlazor()
            .AddAdminApiClient("OSSM:ControlClient").AddAdminListener();
        builder.WebHost.UseStaticWebAssets();

        var app = builder.Build();
        try
        {
            app.UseLocalAccessPolicy(app.Services.GetRequiredService<IOptions<AdminOptions>>().Value.ListenPort);
            app.UseStaticFiles();
            app.UseAntiforgery();
            app.MapStaticAssets(staticAssetsManifestPath);
            app.MapDefaultEndpoints();
            app.MapRazorComponents<App>().AddInteractiveServerRenderMode(
                options => options.ContentSecurityFrameAncestorsPolicy = "'none'");
            return app;
        }
        catch
        {
            ((IDisposable)app).Dispose();
            throw;
        }
    }
}
