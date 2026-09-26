using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OssmSharp.Device.Simulator.Models;
using OssmSharp.Web.Application.Models;

namespace OssmSharp.Web.Api.Tests;

/// <summary>
/// Verifies that invalid listener configuration fails before the service starts listening.
/// </summary>
public sealed class ControlServiceOptionsBoundaryTests
{
    private const string AdminKey = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaA";
    private const string GuestKey = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbQ";
    private static readonly EmptyCertificatePasswordProvider CertificatePasswordProvider = new();

    [Theory]
    [InlineData("1023", false)]
    [InlineData("1024", true)]
    [InlineData("65535", true)]
    [InlineData("65536", false)]
    public void LocalPortBoundariesValidateBeforeTheListenerCanMaterialize(string port, bool accepted)
    {
        using var services = CreateServices([new("OSSM:Control:ControlPort", port)]);

        if (accepted)
        {
            var options = services.GetRequiredService<IOptions<ControlServiceOptions>>().Value;
            var server = services.GetRequiredService<IOptions<KestrelServerOptions>>().Value;

            Assert.Equal(int.Parse(port, System.Globalization.CultureInfo.InvariantCulture), options.ControlPort);
            Assert.Equal(16_384, server.Limits.MaxRequestBodySize);
        }
        else
        {
            var exception = Assert.Throws<OptionsValidationException>(() =>
                _ = services.GetRequiredService<IOptions<KestrelServerOptions>>().Value);

            Assert.Contains(nameof(ControlServiceOptions.ControlPort), exception.Message, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("OSSM:Control:HostName", "guest.example.test", "HostName")]
    [InlineData("OSSM:Control:GuestOrigin", "http://127.0.0.1:5181", "GuestOrigin")]
    [InlineData("OSSM:Control:GuestOrigin", "http://guest.example.test", "GuestOrigin")]
    [InlineData("OSSM:Control:GuestOrigin", "https://localhost:5182", "GuestOrigin")]
    [InlineData("OSSM:Control:GuestOrigin", "http://127.0.0.1:5182/path", "GuestOrigin")]
    public void InvalidLocalHostOrOriginSyntaxFailsBeforeListenerMaterialization(
        string key, string value, string member)
    {
        using var services = CreateServices([new(key, value)]);

        var exception = Assert.Throws<OptionsValidationException>(() =>
            _ = services.GetRequiredService<IOptions<KestrelServerOptions>>().Value);

        Assert.Contains(member, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExternalBindingRequiresHttpsAndAcceptsAnAbsoluteCertificatePath()
    {
        using var insecureServices = CreateServices(
        [
            new("OSSM:Control:BindAddress", "192.0.2.10"),
            new("OSSM:Control:HostName", "control.example.test"),
            new("OSSM:Control:GuestOrigin", "https://guest.example.test")
        ]);
        var insecure = Assert.Throws<OptionsValidationException>(() =>
            _ = insecureServices.GetRequiredService<IOptions<ControlServiceOptions>>().Value);
        Assert.Contains(nameof(ControlServiceOptions.HttpsCertificatePath), insecure.Message, StringComparison.Ordinal);

        using var secureServices = CreateServices(
        [
            new("OSSM:Control:BindAddress", "192.0.2.10"),
            new("OSSM:Control:HostName", "control.example.test"),
            new("OSSM:Control:GuestOrigin", "https://guest.example.test"),
            new("OSSM:Control:HttpsCertificatePath", Path.GetFullPath("control-service.pfx"))
        ]);
        var secure = secureServices.GetRequiredService<IOptions<ControlServiceOptions>>().Value;

        Assert.Equal("192.0.2.10", secure.BindAddress);
        Assert.Equal("https://guest.example.test", secure.GuestOrigin);
        Assert.True(Path.IsPathFullyQualified(secure.HttpsCertificatePath));
    }

    [Theory]
    [InlineData(0, 3, false)]
    [InlineData(1, 2, true)]
    [InlineData(2, 3, false)]
    [InlineData(300, 600, true)]
    [InlineData(301, 600, false)]
    public void TimingBoundsAndTheTwoKeepAliveRuleAreValidated(int keepAlive, int timeout, bool accepted)
    {
        using var services = CreateServices(
        [
            new("OSSM:Control:KeepAliveSeconds", keepAlive.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new("OSSM:Control:ClientTimeoutSeconds", timeout.ToString(System.Globalization.CultureInfo.InvariantCulture))
        ]);

        if (accepted)
        {
            var options = services.GetRequiredService<IOptions<ControlServiceOptions>>().Value;

            Assert.Equal(keepAlive, options.KeepAliveSeconds);
            Assert.Equal(timeout, options.ClientTimeoutSeconds);
        }
        else
        {
            var exception = Assert.Throws<OptionsValidationException>(() =>
                _ = services.GetRequiredService<IOptions<ControlServiceOptions>>().Value);

            Assert.Contains(keepAlive is 2 ? "at least twice" : nameof(ControlServiceOptions.KeepAliveSeconds),
                exception.Message, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("OSSM:Control:GuestWebServiceKey", AdminKey, "distinct")]
    [InlineData("OSSM:Control:AdminWebServiceKey", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "AdminWebServiceKey")]
    [InlineData("OSSM:Control:Unknown", "value", "Unknown")]
    [InlineData("OSSM:Control:SimulatorPath", "old-path", "SimulatorPath")]
    [InlineData("Kestrel:Endpoints:Control:Url", "http://127.0.0.1:5181", "listener overrides")]
    [InlineData("preferHostingUrls", "true", "listener overrides")]
    public void DuplicateMalformedAndUnknownConfigurationIsRejectedAtTheConfiguredBoundary(
        string key, string value, string error)
    {
        using var services = CreateServices([new(key, value)]);

        Exception exception;
        if (key is "OSSM:Control:Unknown" or "OSSM:Control:SimulatorPath")
            exception = Assert.Throws<InvalidOperationException>(() =>
                _ = services.GetRequiredService<IOptions<KestrelServerOptions>>().Value);
        else
            exception = Assert.Throws<OptionsValidationException>(() =>
                _ = services.GetRequiredService<IOptions<KestrelServerOptions>>().Value);

        Assert.Contains(error, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SimulatorEndpointIsBoundToTheSelectedDevice()
    {
        using var services = CreateServices(
            [new("OSSM:Control:SimulatorEndpoint", "tcp://127.0.0.1:1234")],
            includeSimulator: true);

        var options = services.GetRequiredService<IOptions<SimulatedDeviceOptions>>().Value;

        Assert.Equal("tcp://127.0.0.1:1234", options.Endpoint);
    }

    [Theory]
    [InlineData("tcp://localhost:1234")]
    [InlineData("tcp://0.0.0.0:1234")]
    [InlineData("tcp://127.0.0.1:0")]
    public void SimulatorEndpointMustBeValidWhenForwardedFromApi(string endpoint)
    {
        using var services = CreateServices(
            [new("OSSM:Control:SimulatorEndpoint", endpoint)],
            includeSimulator: true);

        var exception = Assert.Throws<OptionsValidationException>(() =>
            _ = services.GetRequiredService<IOptions<SimulatedDeviceOptions>>().Value);

        Assert.Contains(nameof(SimulatedDeviceOptions.Endpoint), exception.Message, StringComparison.Ordinal);
    }

    private static ServiceProvider CreateServices(
        IEnumerable<KeyValuePair<string, string?>> overrides,
        bool includeSimulator = false)
    {
        var values = new Dictionary<string, string?>
        {
            ["OSSM:Control:BindAddress"] = "127.0.0.1",
            ["OSSM:Control:HostName"] = "localhost",
            ["OSSM:Control:ControlPort"] = "5181",
            ["OSSM:Control:GuestOrigin"] = "http://127.0.0.1:5182",
            ["OSSM:Control:SimulatorEndpoint"] = "tcp://127.0.0.1:1234",
            ["OSSM:Control:KeepAliveSeconds"] = "1",
            ["OSSM:Control:ClientTimeoutSeconds"] = "3",
            ["OSSM:Control:AdminWebServiceKey"] = AdminKey,
            ["OSSM:Control:GuestWebServiceKey"] = GuestKey
        };
        foreach (var pair in overrides)
            values[pair.Key] = pair.Value;
        var collection = new ServiceCollection();
        collection.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
        collection.AddSingleton<IControlServiceCertificatePasswordProvider>(CertificatePasswordProvider);
        collection.AddApiConfiguration().AddApiListener();
        if (includeSimulator)
            collection.AddApiComposition();
        return collection.BuildServiceProvider();
    }

    /// <summary>
    /// Leaves certificate password resolution absent while listener options are validated.
    /// </summary>
    private sealed class EmptyCertificatePasswordProvider : IControlServiceCertificatePasswordProvider
    {
        public string? GetPassword() => null;
    }
}
