using System.Globalization;
using System.Text;
using AwesomeAssertions;
using OssmSharp.Runtime.Oem.Protocol;
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

            var command = new FirmwareCommand(FirmwareSetting.Speed, 12);
            var encoded = Request.Encode(7, "setting.write", "motion.speed", new Dictionary<string, int> { ["value"] = command.Value }, 42);

            Encoding.UTF8.GetString(encoded).Should().Be("""{"v":1,"id":7,"op":"setting.write","path":"motion.speed","args":{"value":12},"lease":42}""");
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
    public void PreservesSuppliedValuesWithoutPatternPolicy(int value, string expected)
    {
        var encoded = Encoding.UTF8.GetString(Request.Encode(1, "setting.write", "motion.pattern", new Dictionary<string, int> { ["value"] = value }));

        encoded.Should().Contain($"\"args\":{{\"value\":{expected}}}");
    }

    [Fact]
    public void RejectsZeroIdentifierAndEmptyOperation()
    {
        var zero = () => Request.Encode(0, "setting.write");
        var empty = () => Request.Encode(1, " ");

        zero.Should().Throw<ArgumentOutOfRangeException>();
        empty.Should().Throw<ArgumentException>();
    }
}
