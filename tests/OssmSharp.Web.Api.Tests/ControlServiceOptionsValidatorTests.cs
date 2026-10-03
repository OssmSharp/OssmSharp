using OssmSharp.Web.Api.Models;
using Xunit;

namespace OssmSharp.Web.Api.Tests;

public sealed class ControlServiceOptionsValidatorTests
{
    private const string AdminKey = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void MissingDeviceIdentifierIsValid(string identifier)
    {
        var options = ValidOptions();
        options.DeviceIdentifier = identifier;

        var result = new ControlServiceOptionsValidator().Validate(null, options);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void MissingAdminServiceKeyFailsValidation()
    {
        var options = ValidOptions();
        options.AdminWebServiceKey = string.Empty;

        var result = new ControlServiceOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, failure => failure.Contains("AdminWebServiceKey", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(2, 3, false)]
    [InlineData(2, 4, true)]
    public void ClientTimeoutRelationshipIsValidated(int keepAliveSeconds, int clientTimeoutSeconds, bool valid)
    {
        var options = ValidOptions();
        options.KeepAliveSeconds = keepAliveSeconds;
        options.ClientTimeoutSeconds = clientTimeoutSeconds;

        var result = new ControlServiceOptionsValidator().Validate(null, options);

        Assert.Equal(valid, result.Succeeded);
    }

    private static ControlServiceOptions ValidOptions() => new()
    {
        DeviceIdentifier = "device-1",
        KeepAliveSeconds = 1,
        ClientTimeoutSeconds = 2,
        GuestControlCheckMilliseconds = 100,
        AdminWebServiceKey = AdminKey,
    };
}
