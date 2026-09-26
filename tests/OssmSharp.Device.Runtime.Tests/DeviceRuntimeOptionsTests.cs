using System.ComponentModel.DataAnnotations;

namespace OssmSharp.Device.Runtime.Tests;

/// <summary>
/// Checks validation of shared device runtime options.
/// </summary>
public sealed class DeviceRuntimeOptionsTests
{
    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    public void RejectsNonPositiveExpectationLifetime(long ticks)
    {
        var options = new DeviceRuntimeOptions
        {
            ExpectationLifetime = TimeSpan.FromTicks(ticks),
        };

        var result = Assert.Single(options.Validate(new ValidationContext(options)));

        Assert.Equal("ExpectationLifetime must be positive.", result.ErrorMessage);
        Assert.Equal([nameof(DeviceRuntimeOptions.ExpectationLifetime)], result.MemberNames);
    }

    [Fact]
    public void AcceptsPositiveExpectationLifetime()
    {
        var options = new DeviceRuntimeOptions
        {
            ExpectationLifetime = TimeSpan.FromTicks(1),
        };

        Assert.Empty(options.Validate(new ValidationContext(options)));
    }
}
