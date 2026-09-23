using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.HostFiltering;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OSSMDotNet.ControlService.Models;

namespace OSSMDotNet.Api.Tests;

/// <summary>
/// Checks service-key, origin, and listener options against secure hosting defaults.
/// </summary>
public sealed class ControlServiceOptionsTests
{
    [Fact]
    public void ServiceKeysMustBeDistinctCanonicalBase64UrlValuesForThirtyTwoBytes()
    {
        var invalid = CreateValidOptions();
        invalid.GuestWebServiceKey = invalid.AdminWebServiceKey;

        var duplicateResults = Validate(invalid);
        Assert.Contains(duplicateResults,
            result => result.MemberNames.Contains(nameof(ControlServiceOptions.AdminWebServiceKey)));

        var nonCanonical = CreateValidOptions();
        nonCanonical.AdminWebServiceKey = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

        var encodingResults = Validate(nonCanonical);
        Assert.Contains(encodingResults,
            result => result.MemberNames.Contains(nameof(ControlServiceOptions.AdminWebServiceKey)));
    }

    [Theory]
    [InlineData('A')]
    [InlineData('E')]
    [InlineData('I')]
    [InlineData('M')]
    [InlineData('Q')]
    [InlineData('U')]
    [InlineData('Y')]
    [InlineData('c')]
    [InlineData('g')]
    [InlineData('k')]
    [InlineData('o')]
    [InlineData('s')]
    [InlineData('w')]
    [InlineData('0')]
    [InlineData('4')]
    [InlineData('8')]
    public void ServiceKeysAcceptEveryCanonicalBase64UrlFinalCharacter(char finalCharacter)
    {
        var options = CreateValidOptions();
        options.AdminWebServiceKey = new string('a', 42) + finalCharacter;

        Assert.Empty(ValidateValid(options));
    }

    [Fact]
    public void DefaultsPreserveSecureLocalHostingAndFastHubLivenessDetection()
    {
        var options = CreateValidOptions();

        Assert.Equal("127.0.0.1", options.BindAddress);
        Assert.Equal("localhost", options.HostName);
        Assert.Equal(1, options.KeepAliveSeconds);
        Assert.Equal(3, options.ClientTimeoutSeconds);
        Assert.Empty(ValidateValid(options));
    }

    [Fact]
    public void ValidatesLocalAndExternalGuestOriginsAndExternalHttpsBindings()
    {
        var external = CreateValidOptions();
        external.BindAddress = "192.0.2.10";
        external.HostName = "control.example.test";
        external.HttpsCertificatePath = @"C:\certificates\control-service.pfx";
        external.GuestOrigin = "https://guest.example.test";
        Assert.Empty(ValidateValid(external));

        var httpExternal = CreateValidOptions();
        httpExternal.GuestOrigin = "http://guest.example.test";
        Assert.Contains(Validate(httpExternal),
            result => result.MemberNames.Contains(nameof(ControlServiceOptions.GuestOrigin)));

        var httpsLoopback = CreateValidOptions();
        httpsLoopback.GuestOrigin = "https://localhost:5182";
        Assert.Contains(Validate(httpsLoopback),
            result => result.MemberNames.Contains(nameof(ControlServiceOptions.GuestOrigin)));

        var insecureExternalBinding = CreateValidOptions();
        insecureExternalBinding.BindAddress = "192.0.2.10";
        insecureExternalBinding.GuestOrigin = "https://guest.example.test";
        Assert.Contains(Validate(insecureExternalBinding),
            result => result.MemberNames.Contains(nameof(ControlServiceOptions.HttpsCertificatePath)));

        var invalidTimeouts = CreateValidOptions();
        invalidTimeouts.KeepAliveSeconds = 2;
        invalidTimeouts.ClientTimeoutSeconds = 3;
        Assert.Contains(Validate(invalidTimeouts),
            result => result.MemberNames.Contains(nameof(ControlServiceOptions.ClientTimeoutSeconds)));
    }

    [Fact]
    public void HostFilteringAllowsOnlyTheConfiguredDirectHost()
    {
        var collection = new ServiceCollection();
        collection.Configure<ControlServiceOptions>(options => options.HostName = "control.example.test");
        using var services = collection.AddApiHostFiltering().BuildServiceProvider();

        var hosts = services.GetRequiredService<IOptions<HostFilteringOptions>>().Value.AllowedHosts;

        Assert.Equal(["control.example.test"], hosts);
    }

    [Fact]
    public void CertificatePasswordIsRejectedFromOrdinaryControlConfiguration()
    {
        var values = new Dictionary<string, string?>
        {
            ["OSSM:Control:BindAddress"] = "127.0.0.1",
            ["OSSM:Control:HostName"] = "localhost",
            ["OSSM:Control:ControlPort"] = "5181",
            ["OSSM:Control:GuestOrigin"] = "http://127.0.0.1:5182",
            ["OSSM:Control:SimulatorPath"] = "simulator",
            ["OSSM:Control:AdminWebServiceKey"] = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaA",
            ["OSSM:Control:GuestWebServiceKey"] = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbQ",
            ["OSSM:Control:HttpsCertificatePassword"] = "not-accepted"
        };
        var collection = new ServiceCollection();
        collection.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
        collection.AddApiConfiguration();
        using var services = collection.BuildServiceProvider();

        Assert.Throws<InvalidOperationException>(() =>
            _ = services.GetRequiredService<IOptions<ControlServiceOptions>>().Value);
    }

    private static List<ValidationResult> Validate(ControlServiceOptions options)
    {
        var results = new List<ValidationResult>();
        Assert.False(Validator.TryValidateObject(options, new(options), results, validateAllProperties: true));
        return results;
    }

    private static List<ValidationResult> ValidateValid(ControlServiceOptions options)
    {
        var results = new List<ValidationResult>();
        Assert.True(Validator.TryValidateObject(options, new(options), results, validateAllProperties: true));
        return results;
    }

    private static ControlServiceOptions CreateValidOptions() => new()
    {
        ControlPort = 5181,
        GuestOrigin = "http://127.0.0.1:5182",
        SimulatorPath = "simulator",
        AdminWebServiceKey = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaA",
        GuestWebServiceKey = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbQ"
    };
}
