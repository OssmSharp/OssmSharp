using FastMoq;
using OssmSharp.Web.GuestWeb.Components.Controls;
using Xunit;

namespace OssmSharp.Web.Api.Tests;

public sealed class SettingSliderTests : MockerTestBase<SettingSlider>
{
    [Fact]
    public void Requested53AtStroke10DisplaysCurrent50Independently()
    {
        ComponentTestAccess.Set(Component, nameof(SettingSlider.Name), "speed");
        ComponentTestAccess.Set(Component, nameof(SettingSlider.SelectedValue), 53);
        ComponentTestAccess.Set(Component, nameof(SettingSlider.SentValue), 53);
        ComponentTestAccess.Set(Component, nameof(SettingSlider.CurrentValue), 50.0);
        Assert.Equal("50", ComponentTestAccess.Get<string>(Component, "CurrentText"));
        Assert.Equal(53, Component.SelectedValue);
        Assert.Equal(53, Component.SentValue);
        Assert.Equal(50.0, Component.CurrentValue);

        ComponentTestAccess.Set(Component, nameof(SettingSlider.CurrentValue), null);
        Assert.Equal("--", ComponentTestAccess.Get<string>(Component, "CurrentText"));
        Assert.Equal(53, Component.SelectedValue);
        Assert.Equal(53, Component.SentValue);
    }

    [Theory]
    [InlineData(0d, "0")]
    [InlineData(12.49, "12.49")]
    [InlineData(12.5, "12.5")]
    [InlineData(12.51, "12.51")]
    [InlineData(49.38, "49.38")]
    [InlineData(54.54545454545455, "54.54545454545455")]
    [InlineData(100.4, "100.4")]
    [InlineData(123.5, "123.5")]
    [InlineData(null, "--")]
    public void CurrentSpeedTextPreservesPrecisionWithoutChangingMeasuredOrRequestedValues(double? measured, string expected)
    {
        ComponentTestAccess.Set(Component, nameof(SettingSlider.Name), "speed");
        ComponentTestAccess.Set(Component, nameof(SettingSlider.CurrentValue), measured);
        ComponentTestAccess.Set(Component, nameof(SettingSlider.SelectedValue), 64);
        ComponentTestAccess.Set(Component, nameof(SettingSlider.SentValue), 64);
        Assert.Equal(expected, ComponentTestAccess.Get<string>(Component, "CurrentText"));
        Assert.Equal(measured, Component.CurrentValue);
        Assert.Equal(64, Component.SelectedValue);
        Assert.Equal(64, Component.SentValue);
    }

    [Theory]
    [InlineData("stroke")]
    [InlineData("depth")]
    [InlineData("sensation")]
    public void NonSpeedCurrentTextPreservesFractionalValue(string name)
    {
        ComponentTestAccess.Set(Component, nameof(SettingSlider.Name), name);
        ComponentTestAccess.Set(Component, nameof(SettingSlider.CurrentValue), 12.5);
        Assert.Equal("12.5", ComponentTestAccess.Get<string>(Component, "CurrentText"));
        Assert.Equal(12.5, Component.CurrentValue);
    }
}
