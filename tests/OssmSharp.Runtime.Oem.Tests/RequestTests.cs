using System.Globalization;
using System.Text;
using AwesomeAssertions;
using Xunit;

namespace OssmSharp.Runtime.Oem.Tests;

public sealed class RequestTests
{
    [Fact]
    public void EncodesRadRequestWithInvariantValuesAndLease()
    {
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");

            var encoded = Request.Encode(7, "setting.write", "motion.speed", new Dictionary<string, double> { ["value"] = 12.5 }, 42);

            Encoding.UTF8.GetString(encoded).Should().Be("""{"v":1,"id":7,"op":"setting.write","path":"motion.speed","args":{"value":12.5},"lease":42}""");
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    [Fact]
    public void OmitsAbsentOptionalFields()
    {
        Encoding.UTF8.GetString(Request.Encode(uint.MaxValue, "control.acquire"))
            .Should().Be("""{"v":1,"id":4294967295,"op":"control.acquire"}""");
    }

    [Theory]
    [InlineData(0, "0")]
    [InlineData(100, "100")]
    [InlineData(int.MaxValue, "2147483647")]
    public void PreservesSuppliedValuesWithoutPatternPolicy(double value, string expected)
    {
        var encoded = Encoding.UTF8.GetString(Request.Encode(1, "setting.write", "motion.pattern", new Dictionary<string, double> { ["value"] = value }));

        encoded.Should().Contain($"\"args\":{{\"value\":{expected}}}");
    }

    [Fact]
    public void RejectsZeroIdentifierEmptyOperationAndNonFiniteArguments()
    {
        var zero = () => Request.Encode(0, "setting.write");
        var empty = () => Request.Encode(1, " ");
        var infinite = () => Request.Encode(1, "setting.write", "motion.speed", new Dictionary<string, double> { ["value"] = double.PositiveInfinity });

        zero.Should().Throw<ArgumentOutOfRangeException>();
        empty.Should().Throw<ArgumentException>();
        infinite.Should().Throw<ArgumentException>();
    }
}
