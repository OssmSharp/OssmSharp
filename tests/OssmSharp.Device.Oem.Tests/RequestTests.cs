using System.Globalization;
using System.Text;
using AwesomeAssertions;
using OssmSharp.Device.Primitives;
using Xunit;

namespace OssmSharp.Device.Oem.Tests;

public sealed class RequestTests
{
    [Theory]
    [InlineData(DeviceControl.Pattern, 0, "set:pattern:0")]
    [InlineData(DeviceControl.Pattern, int.MaxValue, "set:pattern:2147483647")]
    [InlineData(DeviceControl.Speed, 20, "set:speed:20")]
    [InlineData(DeviceControl.Stroke, 0, "set:stroke:0")]
    [InlineData(DeviceControl.Depth, 100, "set:depth:100")]
    [InlineData(DeviceControl.Sensation, 25, "set:sensation:25")]
    [InlineData(DeviceControl.Speed, 12.5, "set:speed:12.5")]
    public void EncodesExactlyOneAssignmentWithoutPatternPolicy(DeviceControl control, double value, string expected)
    {
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");

            Request.Encode(new(control, value)).Should().Equal(Encoding.UTF8.GetBytes(expected));
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }
}
