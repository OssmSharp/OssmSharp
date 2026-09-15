using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using OSSMDotNet.Web.HostRuntime;

namespace OSSMDotNet.Web.ServiceBoundary.Tests;

public sealed class StartupOptionsTests
{
    [Fact]
    public async Task MissingAdminConfigurationStopsTheHostBeforeItCanRun()
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Services.AddAdminConfiguration();
        using var host = builder.Build();

        await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("AdminPort", "80")]
    [InlineData("GuestOrigin", "http://example.com:5182")]
    [InlineData("GuestOrigin", "http://127.0.0.1:5182/path")]
    [InlineData("GuestOrigin", "http://127.0.0.1:5182/?secret=hidden")]
    [InlineData("GuestOrigin", "http://user@127.0.0.1:5182")]
    [InlineData("GuestOrigin", "http://127.0.0.1:5182/#fragment")]
    [InlineData("PipeName", @"bad\pipe")]
    [InlineData("PipeName", "anonymous")]
    [InlineData("GuestServiceToken", "not-a-token")]
    [InlineData("SimulatorPath", "")]
    public void NativeOptionsRejectInvalidPropertiesWithoutDisclosingSecrets(string key, string value)
    {
        var values = ValidAdminConfiguration();
        values[$"OSSM:{key}"] = value;
        using var services = AdminServices(values);

        var error = Assert.Throws<OptionsValidationException>(() => services.GetRequiredService<IOptions<AdminOptions>>().Value);

        Assert.DoesNotContain(values["OSSM:GuestServiceToken"]!, error.Message);
    }

    [Fact]
    public void ValidatedOptionsBindThroughNativeDependencyInjection()
    {
        using var services = AdminServices(ValidAdminConfiguration());

        var options = services.GetRequiredService<IOptions<AdminOptions>>().Value;

        Assert.Equal(5181, options.AdminPort);
        Assert.Equal("http://127.0.0.1:5182", options.GuestOrigin);
        Assert.Equal("test-pipe", options.PipeName);
    }

    [Fact]
    public void AdminConfigurationDoesNotRequireAnOperatorCredential()
    {
        var values = ValidAdminConfiguration();
        using var services = AdminServices(values);

        var options = services.GetRequiredService<IOptions<AdminOptions>>().Value;

        Assert.Equal(5181, options.AdminPort);
        Assert.Equal(values["OSSM:GuestServiceToken"], options.GuestServiceToken);
    }

    [Theory]
    [InlineData("Kestrel:Endpoints:Remote:Url", "http://0.0.0.0:5184")]
    [InlineData("Kestrel:Endpoints:Remote:Url", "http://[::]:5184")]
    [InlineData("Kestrel:Endpoints:Remote:Url", "http://192.0.2.1:5184")]
    [InlineData("Kestrel:Endpoints:Remote:Url", "http://localhost:5184")]
    [InlineData("preferHostingUrls", "true")]
    public async Task AdminRejectsListenerOverridesBeforeStarting(string key, string value)
    {
        var values = ValidAdminConfiguration();
        values[key] = value;
        values["urls"] = "http://0.0.0.0:5184";
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Configuration.AddInMemoryCollection(values);
        builder.Services.AddAdminConfiguration();
        using var host = builder.Build();

        var error = await Assert.ThrowsAsync<OptionsValidationException>(
            () => host.StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains("listener", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(values["OSSM:GuestServiceToken"]!, error.Message);
    }

    [Fact]
    public void GuestRejectsAccidentallyProvisionedDeviceConfiguration()
    {
        var values = new Dictionary<string, string?>
        {
            ["OSSM:GuestPort"] = "5182",
            ["OSSM:PipeName"] = "test-pipe",
            ["OSSM:GuestServiceToken"] = new string('b', 43),
            ["OSSM:SimulatorPath"] = "private-simulator-path"
        };
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
        services.AddGuestConfiguration();
        using var provider = services.BuildServiceProvider();

        var error = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IOptions<GuestOptions>>().Value);

        Assert.DoesNotContain(values["OSSM:SimulatorPath"]!, error.Message);
    }

    private static ServiceProvider AdminServices(Dictionary<string, string?> values)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
        services.AddAdminConfiguration();
        return services.BuildServiceProvider();
    }

    private static Dictionary<string, string?> ValidAdminConfiguration() => new()
    {
        ["OSSM:AdminPort"] = "5181",
        ["OSSM:GuestOrigin"] = "http://127.0.0.1:5182",
        ["OSSM:PipeName"] = "test-pipe",
        ["OSSM:GuestServiceToken"] = new string('b', 43),
        ["OSSM:SimulatorPath"] = @"D:\simulator\ossm-sim-bridge.exe"
    };
}
