using OSSMDotNet.Models;

namespace OSSMDotNet.ControlCore.Tests;

public sealed class PrototypeEngineBoundaryTests
{
    public static IEnumerable<object?[]> InvalidCreationScopes =>
    [
        [null],
        [""],
        [" \t"],
        [new string('s', 257)]
    ];

    [Theory]
    [MemberData(nameof(InvalidCreationScopes))]
    public async Task InvalidCreationScopeDoesNotCreateAnInvitationOrReachTheBridge(string? scope)
    {
        await using var rig = new Rig();
        var before = rig.Engine.Snapshot;

        var rejection = Assert.Throws<ControlRejectedException>(() =>
            rig.Engine.CreateInvitation(Guid.NewGuid(), new Uri("http://localhost:8080"), scope!));

        Assert.Equal("invalid-attempt", rejection.Code);
        Assert.Equal(before, rig.Engine.Snapshot);
        Assert.Empty(rig.Bridge.Calls);
    }

    [Fact]
    public async Task ExactMaximumCreationScopeCreatesALocalInvitationWithoutChangingControlState()
    {
        await using var rig = new Rig();
        var before = rig.Engine.Snapshot;

        var invitation = rig.Engine.CreateInvitation(
            Guid.NewGuid(), new Uri("http://localhost:8080"), new string('s', 256));

        Assert.True(invitation.LocalOnly);
        Assert.StartsWith($"http://localhost:8080/join/{invitation.SessionId}#invite=", invitation.Url,
            StringComparison.Ordinal);
        Assert.Equal(before, rig.Engine.Snapshot);
        Assert.Empty(rig.Bridge.Calls);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("speed-below-minimum")]
    [InlineData("speed-above-maximum")]
    [InlineData("depth-below-minimum")]
    [InlineData("depth-above-maximum")]
    public async Task InvalidLimitTuplesRejectWithoutChangingStateOrReachingTheBridge(string boundary)
    {
        await using var rig = new Rig();
        var before = rig.Engine.Snapshot;
        ControlLimits? limits = boundary switch
        {
            "null" => null,
            "speed-below-minimum" => new(-1, 100),
            "speed-above-maximum" => new(101, 100),
            "depth-below-minimum" => new(100, -1),
            "depth-above-maximum" => new(100, 101),
            _ => throw new ArgumentOutOfRangeException(nameof(boundary))
        };

        var rejection = Assert.Throws<ControlRejectedException>(() => rig.Engine.SetLimits(limits!));

        Assert.Equal("invalid-limits", rejection.Code);
        Assert.Equal(before, rig.Engine.Snapshot);
        Assert.Empty(rig.Bridge.Calls);
    }

    [Fact]
    public async Task AttemptCapacityRejectsTheNextNewAttemptWithoutChangingControlState()
    {
        await using var rig = new Rig();
        var origin = new Uri("http://localhost:8080");
        for (var count = 0; count < 4096; count++)
            rig.Engine.CreateInvitation(Guid.NewGuid(), origin, "operator");
        var before = rig.Engine.Snapshot;

        var rejection = Assert.Throws<ControlRejectedException>(() =>
            rig.Engine.CreateInvitation(Guid.NewGuid(), origin, "operator"));

        Assert.Equal("attempt-capacity", rejection.Code);
        Assert.Equal(before, rig.Engine.Snapshot);
        Assert.Empty(rig.Bridge.Calls);
    }
}
