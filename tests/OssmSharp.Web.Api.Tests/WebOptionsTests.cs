using System.ComponentModel.DataAnnotations;
using FastMoq;
using OssmSharp.Web.Models;
using Xunit;

namespace OssmSharp.Web.Api.Tests;

public sealed class WebOptionsTests : MockerTestBase<WebOptions>
{
    [Fact]
    public void DefaultPortsMatchTheStandaloneLauncher()
    {
        Assert.Equal(5003, Component.ApiPort);
        Assert.Equal(5002, Component.AdminPort);
        Assert.Equal(5001, Component.GuestPort);
    }

    [Theory]
    [InlineData(5001, 5001, 5003)]
    [InlineData(5001, 5002, 5001)]
    [InlineData(5003, 5002, 5002)]
    [InlineData(1023, 5002, 5001)]
    [InlineData(5003, 65536, 5001)]
    [InlineData(5003, 5002, 0)]
    public void InvalidOrDuplicatePortsAreRejected(int apiPort, int adminPort, int guestPort)
    {
        Component.ApiPort = apiPort;
        Component.AdminPort = adminPort;
        Component.GuestPort = guestPort;
        var errors = new List<ValidationResult>();

        Assert.False(Validator.TryValidateObject(Component, new ValidationContext(Component), errors, validateAllProperties: true));
        Assert.NotEmpty(errors);
        Assert.True(new WebOptionsValidator().Validate(null, Component).Failed);
    }
}
