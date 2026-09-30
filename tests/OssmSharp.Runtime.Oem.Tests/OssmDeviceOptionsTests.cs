using System.ComponentModel.DataAnnotations;
using AwesomeAssertions;
using Xunit;

namespace OssmSharp.Runtime.Oem.Tests;

public sealed class OssmDeviceOptionsTests
{
    [Fact]
    public void RequestOptionsPreserveDefaultLimits()
    {
        var options = new OssmDeviceOptions();
        var results = new List<ValidationResult>();

        options.MaximumRequestAttempts.Should().Be(3);
        options.ResponseTimeout.Should().Be(TimeSpan.FromSeconds(2));
        Validator.TryValidateObject(options, new(options), results).Should().BeTrue();
        results.Should().BeEmpty();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void RequestOptionsRejectNonpositiveAttemptLimit(int attempts)
    {
        var options = new OssmDeviceOptions { MaximumRequestAttempts = attempts };
        var results = new List<ValidationResult>();

        Validator.TryValidateObject(options, new(options), results).Should().BeFalse();
        results.Should().ContainSingle().Which.MemberNames.Should().Equal(nameof(OssmDeviceOptions.MaximumRequestAttempts));
    }

    [Theory]
    [InlineData(-1d)]
    [InlineData(0d)]
    [InlineData(4294967295d)]
    public void RequestOptionsRejectUnsupportedResponseTimeout(double milliseconds)
    {
        var options = new OssmDeviceOptions { ResponseTimeout = TimeSpan.FromMilliseconds(milliseconds) };
        var results = new List<ValidationResult>();

        Validator.TryValidateObject(options, new(options), results).Should().BeFalse();
        results.Should().ContainSingle().Which.MemberNames.Should().Equal(nameof(OssmDeviceOptions.ResponseTimeout));
    }

    [Fact]
    public void RequestOptionsReturnAllInvalidProperties()
    {
        var options = new OssmDeviceOptions { MaximumRequestAttempts = 0, ResponseTimeout = TimeSpan.Zero };
        var results = new List<ValidationResult>();

        Validator.TryValidateObject(options, new(options), results).Should().BeFalse();
        results.SelectMany(result => result.MemberNames).Should().Equal(
            nameof(OssmDeviceOptions.MaximumRequestAttempts),
            nameof(OssmDeviceOptions.ResponseTimeout));
    }
}
