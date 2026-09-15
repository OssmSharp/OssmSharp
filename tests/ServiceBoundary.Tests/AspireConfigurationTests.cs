using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OSSMDotNet.Web.HostRuntime;

namespace OSSMDotNet.Web.ServiceBoundary.Tests;

public sealed class AspireConfigurationTests
{
    [Fact]
    public void AdminReadsTheAspireConfigurationWithoutAStdinLauncher()
    {
        var values = AdminValues();

        var configuration = ReadAdmin(values);

        Assert.Equal(5181, configuration.AdminPort);
        Assert.Equal("http://127.0.0.1:5182", configuration.GuestOrigin);
        Assert.Equal(values["OSSM:PipeName"], configuration.PipeName);
        Assert.Equal(values["OSSM:GuestServiceToken"], configuration.GuestServiceToken);
        Assert.Equal(values["OSSM:SimulatorPath"], configuration.SimulatorPath);
    }

    [Fact]
    public void GuestNeedsOnlyItsOwnPortAndForwardingCredential()
    {
        var values = GuestValues();

        var configuration = ReadGuest(values);

        Assert.Equal(5182, configuration.GuestPort);
        Assert.Equal(values["OSSM:PipeName"], configuration.PipeName);
        Assert.Equal(values["OSSM:GuestServiceToken"], configuration.GuestServiceToken);
    }

    [Theory]
    [InlineData("OSSM:AdminPort", "80")]
    [InlineData("OSSM:AdminPort", "65536")]
    [InlineData("OSSM:AdminPort", "5181.0")]
    [InlineData("OSSM:GuestOrigin", "http://0.0.0.0:5182")]
    [InlineData("OSSM:GuestOrigin", "http://example.com:5182")]
    [InlineData("OSSM:GuestOrigin", "http://127.0.0.1:5182/path")]
    [InlineData("OSSM:GuestOrigin", "http://127.0.0.1:5182/?secret=hidden")]
    [InlineData("OSSM:GuestOrigin", "http://user@127.0.0.1:5182")]
    [InlineData("OSSM:GuestOrigin", "http://127.0.0.1:5182/#fragment")]
    [InlineData("OSSM:GuestServiceToken", "short")]
    [InlineData("OSSM:PipeName", @"directory\pipe")]
    [InlineData("OSSM:PipeName", "anonymous")]
    [InlineData("OSSM:SimulatorPath", "")]
    public void InvalidBootstrapFailsClosedWithoutEchoingValues(string key, string value)
    {
        var values = AdminValues();
        values[key] = value;

        if (key == "OSSM:AdminPort" && value == "5181.0")
            Assert.Throws<InvalidOperationException>(() => ReadAdmin(values));
        else
        {
            var error = Assert.Throws<OptionsValidationException>(() => ReadAdmin(values));
            Assert.DoesNotContain(values["OSSM:GuestServiceToken"]!, error.Message);
        }
    }

    [Fact]
    public void MissingConfigurationCannotUseSilentDefaults()
    {
        Assert.Throws<OptionsValidationException>(() => ReadAdmin([]));
        Assert.Throws<OptionsValidationException>(() => ReadGuest([]));
    }

    [Fact]
    public void GuestRejectsAccidentallyProvisionedDeviceConfiguration()
    {
        var values = GuestValues();
        values["OSSM:SimulatorPath"] = "private-simulator-path";

        var error = Assert.Throws<InvalidOperationException>(() => ReadGuest(values));
        Assert.DoesNotContain(values["OSSM:SimulatorPath"]!, error.Message);
    }

    private static AdminOptions ReadAdmin(Dictionary<string, string?> values)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
        services.AddAdminConfiguration();
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<AdminOptions>>().Value;
    }

    private static GuestOptions ReadGuest(Dictionary<string, string?> values)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
        services.AddGuestConfiguration();
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<GuestOptions>>().Value;
    }

    private static Dictionary<string, string?> AdminValues() => new()
    {
        ["OSSM:AdminPort"] = "5181",
        ["OSSM:GuestOrigin"] = "http://127.0.0.1:5182",
        ["OSSM:PipeName"] = "ossm-test-bootstrap",
        ["OSSM:GuestServiceToken"] = new string('b', 43),
        ["OSSM:SimulatorPath"] = @"D:\simulator\ossm-sim-bridge.exe"
    };

    private static Dictionary<string, string?> GuestValues() => new()
    {
        ["OSSM:GuestPort"] = "5182",
        ["OSSM:PipeName"] = "ossm-test-bootstrap",
        ["OSSM:GuestServiceToken"] = new string('b', 43)
    };
}
