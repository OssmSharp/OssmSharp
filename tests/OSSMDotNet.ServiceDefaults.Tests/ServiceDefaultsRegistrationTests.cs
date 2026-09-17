using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using OSSMDotNet.ServiceDefaults;

namespace OSSMDotNet.ServiceDefaults.Tests;

public sealed class ServiceDefaultsRegistrationTests
{
    [Fact]
    public async Task ValidGuestConfigurationResolvesHealthAndLocalBlazorSafetyServices()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = [],
            EnvironmentName = Environments.Development
        });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OSSM:Guest:GuestPort"] = "5182"
        });
        builder.AddServiceDefaults();
        builder.Services.AddGuestConfiguration().AddLocalBlazor();
        await using var app = builder.Build();

        var report = await app.Services.GetRequiredService<HealthCheckService>()
            .CheckHealthAsync(TestContext.Current.CancellationToken);
        var json = app.Services.GetRequiredService<IOptions<JsonOptions>>().Value.SerializerOptions;
        var routes = app.Services.GetRequiredService<IOptions<RouteHandlerOptions>>().Value;

        Assert.Equal(HealthStatus.Healthy, report.Status);
        Assert.Equal(5182, app.Services.GetRequiredService<IOptions<GuestOptions>>().Value.GuestPort);
        Assert.Same(TimeProvider.System, app.Services.GetRequiredService<TimeProvider>());
        Assert.NotNull(app.Services.GetService<MeterProvider>());
        Assert.NotNull(app.Services.GetService<TracerProvider>());
        Assert.False(routes.ThrowOnBadRequest);
        Assert.False(json.PropertyNameCaseInsensitive);
        Assert.Equal(12, json.MaxDepth);
        Assert.False(json.AllowDuplicateProperties);
        Assert.True(json.RespectRequiredConstructorParameters);
        Assert.True(json.RespectNullableAnnotations);
    }

    [Theory]
    [InlineData("1023")]
    [InlineData("65536")]
    [InlineData("5182", "Unexpected")]
    public void InvalidGuestConfigurationFailsAtOptionsValidationBoundary(
        string port, string? unsupportedSetting = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["OSSM:Guest:GuestPort"] = port
        };
        if (unsupportedSetting is not null)
            values["OSSM:Guest:Unsupported"] = unsupportedSetting;
        using var services = CreateOptionsServices(values, static collection => collection.AddGuestConfiguration());

        Exception exception;
        if (unsupportedSetting is null)
            exception = Assert.Throws<OptionsValidationException>(() =>
                _ = services.GetRequiredService<IOptions<GuestOptions>>().Value);
        else
            exception = Assert.Throws<InvalidOperationException>(() =>
                _ = services.GetRequiredService<IOptions<GuestOptions>>().Value);

        Assert.Contains(unsupportedSetting is null ? nameof(GuestOptions.GuestPort) : "Unsupported", exception.Message,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("1024")]
    [InlineData("65535")]
    public void ExactAdminPortBoundsAreAcceptedByRegisteredValidation(string port)
    {
        using var services = CreateOptionsServices(
            new Dictionary<string, string?> { ["OSSM:Admin:AdminPort"] = port },
            static collection => collection.AddAdminConfiguration());

        var options = services.GetRequiredService<IOptions<AdminOptions>>().Value;

        Assert.Equal(int.Parse(port, System.Globalization.CultureInfo.InvariantCulture), options.AdminPort);
    }

    [Theory]
    [InlineData("Kestrel:Endpoints:Control:Url", "http://127.0.0.1:5181")]
    [InlineData("preferHostingUrls", "true")]
    [InlineData("OSSM:Admin:Unexpected", "value")]
    public void AdminListenerOverridesAndUnknownConfigurationAreRejected(string key, string value)
    {
        var values = new Dictionary<string, string?>
        {
            ["OSSM:Admin:AdminPort"] = "5181",
            [key] = value
        };
        using var services = CreateOptionsServices(values, static collection => collection.AddAdminConfiguration());

        Exception exception;
        if (key.StartsWith("OSSM:Admin", StringComparison.Ordinal))
            exception = Assert.Throws<InvalidOperationException>(() =>
                _ = services.GetRequiredService<IOptions<AdminOptions>>().Value);
        else
            exception = Assert.Throws<OptionsValidationException>(() =>
                _ = services.GetRequiredService<IOptions<AdminOptions>>().Value);

        Assert.Contains(key.StartsWith("OSSM:Admin", StringComparison.Ordinal)
            ? "Unexpected"
            : "Admin listener overrides are not supported", exception.Message, StringComparison.Ordinal);
    }

    private static ServiceProvider CreateOptionsServices(
        IEnumerable<KeyValuePair<string, string?>> values, Action<IServiceCollection> configure)
    {
        var collection = new ServiceCollection();
        collection.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
        configure(collection);
        return collection.BuildServiceProvider();
    }
}
