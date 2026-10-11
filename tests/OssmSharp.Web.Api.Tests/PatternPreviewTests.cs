using FastMoq;
using OssmSharp.Web.Common.Components;
using Xunit;

namespace OssmSharp.Web.Api.Tests;

public sealed class PatternPreviewTests : MockerTestBase<PatternPreview>
{
    public static TheoryData<int?, double?, int?, int?, int?, string, bool, bool> PartialSettings =>
        new()
        {
            { null, null, null, null, null, "Unknown preview at Unknown speed, Unknown stroke, Unknown depth, and Unknown sensation", false, false },
            { null, 17, null, null, null, "Unknown preview at 17% speed, Unknown stroke, Unknown depth, and Unknown sensation", false, false },
            { null, 17, null, 47, null, "Unknown preview at 17% speed, Unknown stroke, 47% depth, and Unknown sensation", false, false },
            { null, 17, 36, null, null, "Unknown preview at 17% speed, 36% stroke, Unknown depth, and Unknown sensation", false, false },
            { null, 17, 36, 47, 59, "Unknown preview at 17% speed, 36% stroke, 47% depth, and 59% sensation", true, false },
            { 99, 17, 36, 47, 59, "Pattern 99 preview at 17% speed, 36% stroke, 47% depth, and 59% sensation", true, false },
            { 0, null, 36, 47, null, "Simple stroke preview at Unknown speed, 36% stroke, 47% depth, and Unknown sensation", true, false },
            { 0, 0, 36, 47, 59, "Simple stroke preview at 0% speed, 36% stroke, 47% depth, and 59% sensation", true, false },
            { 5, 28, 36, 47, 59, "Stop / go preview at 28% speed, 36% stroke, 47% depth, and 59% sensation", true, true },
        };

    [Theory]
    [MemberData(nameof(PartialSettings))]
    public void PartialRequestedSettingsPreserveUnknownsAndLabels(
        int? pattern, double? speed, int? stroke, int? depth, int? sensation, string label, bool range, bool animation)
    {
        SetParameters(pattern, speed, stroke, depth, sensation);
        Assert.Equal(label, Read<string>("PreviewLabel"));
        Assert.Equal(label[..label.IndexOf(" preview", StringComparison.Ordinal)], Read<string>("PatternLabel"));
        Assert.Equal(range, Read<int?>("BoundedStroke") is not null && Read<int?>("BoundedDepth") is not null);
        Assert.Equal(animation, HasTravel());
        Assert.Equal(stroke, Read<int?>("BoundedStroke"));
        Assert.Equal(depth, Read<int?>("BoundedDepth"));
        Assert.Equal(sensation, Read<int?>("BoundedSensation"));
    }

    [Theory]
    [InlineData(50, 10, 10, "0.8s")]
    [InlineData(50, 80, 100, "0.8s")]
    [InlineData(50, 10, 80, "0.8s")]
    [InlineData(100, 10, 80, "0.4s")]
    [InlineData(0, 10, 80, null)]
    [InlineData(5, 0, 80, null)]
    [InlineData(5, 10, 0, null)]
    [InlineData(53, 10, 10, "0.7547s")]
    [InlineData(25, 10, 10, "1.6s")]
    [InlineData(30, 1, 1, "1.3333s")]
    [InlineData(100, 1, 1, "0.4s")]
    [InlineData(100, 100, 100, "0.4s")]
    [InlineData(0, 10, 20, null)]
    [InlineData(50, 10, 20, "0.8s")]
    public void IllustrativeTimingUsesRequestedSpeedAndTravelEligibility(double speed, int stroke, int depth, string? duration)
    {
        SetParameters(0, speed, stroke, depth, 50);
        Assert.Equal(duration is not null, HasTravel());
        if (duration is not null)
            Assert.Equal(duration, Read<string>("AnimationDuration"));
    }

    [Fact]
    public void AttachmentAndInsertionUseTheSameLengthScale()
    {
        foreach (var (attachment, rail, stroke, depth, minimum, maximum) in new[]
        {
            (24.0, 40.0, 100, 100, 0.0, 24.0),
            (24.0, 40.0, 20, 60, 0.0, 8.0),
            (24.0, 40.0, 0, 50, 4.0, 4.0),
            (0.0, 40.0, 100, 100, 0.0, 0.0),
            (80.0, 40.0, 100, 100, 40.0, 80.0),
        })
        {
            ComponentTestAccess.Set(Component, nameof(PatternPreview.AttachmentLength), attachment);
            ComponentTestAccess.Set(Component, nameof(PatternPreview.RailLength), rail);
            SetParameters(0, 50, stroke, depth, 50);
            Assert.Equal(attachment / rail, Read<double>("AttachmentTravel"));
            Assert.Equal(minimum, Read<double>("MinimumInsertionCm"), precision: 6);
            Assert.Equal(maximum, Read<double>("MaximumInsertionCm"), precision: 6);
        }
    }

    [Fact]
    public void SevenProfilesPreserveNormalizationDirectionAndPatternPartitions()
    {
        var profiles = new List<PreviewProfile>();
        for (var pattern = 0; pattern <= 6; pattern++)
        {
            SetParameters(pattern, 50, 10, 20, 75);
            var positions = Read<double[]>("_positions");
            var times = Read<double[]>("_times");
            Assert.Equal(positions.Length, times.Length);
            Assert.Equal(0, times[0]);
            Assert.Equal(1, times[^1]);
            Assert.All(times.Zip(times.Skip(1)), pair => Assert.True(pair.First < pair.Second));
            Assert.All(positions, position => Assert.InRange(position, 0, 1));
            Assert.True(positions[1] > positions[0], "The first segment inserts toward the front.");
            Assert.True(HasTravel());
            var profile = Profile();
            Assert.DoesNotContain(profiles, previous => previous.SameMotion(profile));
            profiles.Add(profile);
            if (pattern == 5)
            {
                var groups = new List<int>();
                var strokes = 0;
                for (var index = 1; index < positions.Length; index++)
                {
                    if (positions[index] > positions[0])
                        strokes++;
                    if (positions[index] == positions[0] && positions[index - 1] == positions[0])
                    {
                        groups.Add(strokes);
                        strokes = 0;
                    }
                }
                Assert.Equal([1, 2, 3, 4, 5, 4, 3, 2], groups);
            }
        }
        Assert.Equal(7, profiles.Count);
        for (var pattern = 0; pattern <= 5; pattern++)
        {
            SetParameters(pattern, 50, 10, 20, 0);
            var zero = Profile();
            SetParameters(pattern, 50, 10, 20, 100);
            if (pattern == 0)
                Assert.True(zero.SameMotion(Profile()));
            else
                Assert.False(zero.SameMotion(Profile()));
        }
        foreach (var sensation in new[] { 0, 100 })
        {
            SetParameters(6, 50, 10, 20, sensation);
            Assert.False(HasTravel());
            Assert.All(Read<double[]>("_positions"), position => Assert.Equal(sensation == 0 ? 0 : 1, position));
        }
        SetParameters(0, 100, 100, 100, 50);
        Assert.True(Read<double>("End") > Read<double>("Start"), "Full-range selection permits travel.");
        Assert.True(HasTravel());
        SetParameters(0, 100, 0, 80, 50);
        Assert.Equal(Read<double>("Start"), Read<double>("End"));
        Assert.False(HasTravel());
    }

    private T Read<T>(string name) => ComponentTestAccess.Get<T>(Component, name);

    private void SetParameters(int? pattern, double? speed, int? stroke, int? depth, int? sensation)
    {
        // Reflection avoids BL0005 and deliberately targets the existing component parameters.
        ComponentTestAccess.Set(Component, nameof(PatternPreview.Pattern), pattern);
        ComponentTestAccess.Set(Component, nameof(PatternPreview.Speed), speed);
        ComponentTestAccess.Set(Component, nameof(PatternPreview.Stroke), stroke);
        ComponentTestAccess.Set(Component, nameof(PatternPreview.Depth), depth);
        ComponentTestAccess.Set(Component, nameof(PatternPreview.Sensation), sensation);
        ComponentTestAccess.Invoke(Component, "OnParametersSet");
    }

    private bool HasTravel() => ComponentTestAccess.HasElement(Component, "animate");

    private PreviewProfile Profile() => new(Read<double[]>("_positions"), Read<double[]>("_times"), Read<string>("AnimationSplines"), Read<double>("_cycles"));

    private sealed record PreviewProfile(double[] Positions, double[] Times, string Splines, double Cycles)
    {
        internal bool SameMotion(PreviewProfile other) => Positions.SequenceEqual(other.Positions)
            && Times.SequenceEqual(other.Times) && Splines == other.Splines && Cycles == other.Cycles;
    }
}
