namespace OSSMDotNet.WebCommon.Tests;

public sealed class MotionMonitorTests
{
    [Fact]
    public async Task EnabledCycleDoesNotFlickerIdleBetweenIndividualMoves()
    {
        var moving = await Render(true, true, 30, enabled: true);
        var turnaround = await Render(true, false, 30, enabled: true);
        Assert.DoesNotContain(">IDLE<", moving);
        Assert.DoesNotContain(">IDLE<", turnaround);
        var stopped = await Render(true, false, 30);
        Assert.Contains(">IDLE<", stopped);
        var retracting = await Render(true, true, 30);
        Assert.DoesNotContain(">IDLE<", retracting);
    }

    [Fact]
    public async Task SimulatorCaptionIsOnlyShownForASimulator()
    {
        var device = await Render(true, false, 30, simulator: false);
        var simulator = await Render(true, false, 30, simulator: true);
        Assert.DoesNotContain("Position from the simulator", device);
        Assert.Contains("Position from the simulator", simulator);
        Assert.DoesNotContain("OSSM-RS", simulator);
    }

    [Fact]
    public async Task MissingFeedbackNeverDrawsMotionOrInventsPosition()
    {
        var html = await Render(false, true, 123.4);
        Assert.Contains("OFFLINE", html);
        Assert.DoesNotContain("123.4", html);
        Assert.DoesNotContain("class=\"carriage\"", html);
    }

    [Fact]
    public async Task ActualFeedbackDrivesBothThePositionMarkerAndReadableValues()
    {
        var first = await Render(true, true, 12.5);
        var second = await Render(true, false, 90.5);
        Assert.Contains("RUNNING", first);
        Assert.Contains("12.5", first);
        Assert.Contains("class=\"carriage\"", first);
        Assert.Contains("IDLE", second);
        Assert.Contains("90.5", second);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public async Task HeaderCanBeHiddenWhenTheParentOwnsTheFeedbackLabel()
    {
        var html = await Render(true, false, 30, showHeading: false);

        Assert.DoesNotContain("Live feedback", html);
        Assert.DoesNotContain("Connected", html);
    }

    [Fact]
    public async Task GuestStatusCanHideNumericReadingsWhileRetainingLiveStatusAndRail()
    {
        var html = await Render(
            fresh: true,
            moving: true,
            position: 12.5,
            simulator: true,
            heading: "Live Status",
            showReadings: false,
            showSimulator: true);

        Assert.Contains("Live Status", html);
        Assert.Contains("SIMULATOR", html);
        Assert.Contains(">RUNNING<", html);
        Assert.Contains("motion-rail", html);
        Assert.Contains("class=\"carriage\"", html);
        Assert.DoesNotContain("data-testid=\"live-position\"", html);
        Assert.DoesNotContain("data-testid=\"live-velocity\"", html);
        Assert.DoesNotContain(">Position<", html);
        Assert.DoesNotContain("Position from the simulator", html);
    }

    private static async Task<string> Render(
        bool fresh,
        bool moving,
        double position,
        bool enabled = false,
        bool simulator = false,
        bool showHeading = true,
        string? heading = null,
        bool showReadings = true,
        bool showSimulator = false)
    {
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var parameters = new Dictionary<string, object?>
            {
                [nameof(MotionMonitor.Fresh)] = fresh,
                [nameof(MotionMonitor.Moving)] = moving,
                [nameof(MotionMonitor.Enabled)] = enabled,
                [nameof(MotionMonitor.IsSimulator)] = simulator,
                [nameof(MotionMonitor.Position)] = position,
                [nameof(MotionMonitor.Velocity)] = moving ? 5.5 : 0d
            };
            if (!showHeading)
            {
                parameters["ShowHeading"] = false;
            }
            if (heading is not null)
            {
                parameters["Heading"] = heading;
            }
            if (!showReadings)
            {
                parameters["ShowReadings"] = false;
            }
            if (showSimulator)
            {
                parameters["ShowSimulator"] = true;
            }

            var component = await renderer.RenderComponentAsync<MotionMonitor>(ParameterView.FromDictionary(parameters));
            return component.ToHtmlString();
        });
    }
}
