using OSSMDotNet.GuestWeb.Components.Pages;

namespace OSSMDotNet.GuestWeb.Tests;

/// <summary>
/// Checks invitation admission, guest authority, and UI updates through the rendered join page.
/// </summary>
public sealed class JoinPageBehaviorTests
{
    public static IEnumerable<object?[]> InvalidInvitations =>
    [
        [null],
        [" "],
        [new string('a', 129)]
    ];

    [Theory]
    [InlineData("proof", false)]
    [InlineData(null, false)]
    [InlineData("proof", true)]
    public async Task CompletedGuestLifecycleDoesNotScheduleMoreRenders(string? invitation, bool rejectJoin)
    {
        using var cancellationSource = CreateTimeout();
        await using var fixture = await PageControlHubFixture.StartAsync(cancellationSource.Token);
        if (rejectJoin)
            fixture.Reject("Join", "invalid-invitation");
        await using var module = new RecordedJsModule { Invitation = invitation };
        await using var services = CreateServices(module, fixture.CreateGuestClient());
        await using var renderer = new ComponentRenderer(services, services.GetRequiredService<ILoggerFactory>())
        {
            InvokeAfterRender = false
        };
        var (page, componentId) = await renderer.RenderComponentAsync<Join>(Parameters("session-1"));
        var lifecycle = (IHandleAfterRender)page;

        await renderer.Dispatcher.InvokeAsync(lifecycle.OnAfterRenderAsync);
        await renderer.Dispatcher.InvokeAsync(lifecycle.OnAfterRenderAsync);
        var renderCount = renderer.RenderCount;
        await renderer.Dispatcher.InvokeAsync(lifecycle.OnAfterRenderAsync);
        await renderer.Dispatcher.InvokeAsync(lifecycle.OnAfterRenderAsync);

        Assert.Equal(renderCount, renderer.RenderCount);
        Assert.Equal(1, module.InvocationCount("takeInvitation"));
        Assert.Equal(invitation is not null && !rejectJoin ? 1 : 0, module.InvocationCount("attach"));
        Assert.DoesNotContain(fixture.Invocations, invocation => invocation.Method is "Submit" or "GuestStop");
        if (invitation is null || rejectJoin)
        {
            Assert.Contains("This invitation is invalid or has been replaced.", await GetTextContentAsync(renderer, componentId));
            Assert.Null(renderer.GetElementAttributeValue(
                componentId, "rw-alert", "data-testid", "guest-disconnected", "variant"));
            Assert.Equal(new BrowserReply(false, "not-joined"), await page.StopRun());
        }
    }

    [Fact]
    public async Task AuthorityIsSentOnlyAfterAttachmentAndUsesTheLatestStatus()
    {
        using var cancellationSource = CreateTimeout();
        await using var fixture = await PageControlHubFixture.StartAsync(cancellationSource.Token);
        await using var module = new RecordedJsModule { Invitation = "proof" };
        await using var services = CreateServices(module, fixture.CreateGuestClient());
        await using var renderer = new ComponentRenderer(services, services.GetRequiredService<ILoggerFactory>())
        {
            InvokeAfterRender = false
        };
        var (page, componentId) = await renderer.RenderComponentAsync<Join>(Parameters("session-1"));
        var lifecycle = (IHandleAfterRender)page;
        await renderer.Dispatcher.InvokeAsync(lifecycle.OnAfterRenderAsync);
        await fixture.SendGuestStatusAsync(PageControlHubFixture.ReadyGuestStatus(1), cancellationSource.Token);
        await renderer.WaitForTextContentAsync(componentId, "Control enabled.", cancellationSource.Token);
        await fixture.SendGuestStatusAsync(PageControlHubFixture.LockedGuestStatus(2) with { Grant = null }, cancellationSource.Token);
        await renderer.WaitForTextContentAsync(componentId, "Locked by the operator. Sending is disabled.", cancellationSource.Token);

        Assert.Equal(0, module.InvocationCount("updateAuthority"));
        await renderer.Dispatcher.InvokeAsync(lifecycle.OnAfterRenderAsync);

        Assert.Equal(1, module.InvocationCount("attach"));
        var update = Assert.Single(module.Invocations, invocation => invocation.Identifier == "updateAuthority");
        var authority = System.Text.Json.JsonSerializer.SerializeToElement(Assert.Single(update.Arguments));
        Assert.True(authority.GetProperty("joined").GetBoolean());
        Assert.False(authority.GetProperty("ready").GetBoolean());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, authority.GetProperty("grant").ValueKind);
        Assert.Equal(new BrowserReply(false, "not-ready"), await page.SubmitRun(ValidInput()));
        Assert.Equal(["SubscribeGuest", "Join"], fixture.Invocations.Select(invocation => invocation.Method));
    }

    [Theory]
    [MemberData(nameof(InvalidInvitations))]
    public async Task MissingOrInvalidInvitationIsTakenOnceAndFailsClosedWithoutOpeningTheGuestHub(string? invitation)
    {
        using var cancellationSource = CreateTimeout();
        await using var fixture = await PageControlHubFixture.StartAsync(cancellationSource.Token);
        await using var module = new RecordedJsModule { Invitation = invitation };
        await using var services = CreateServices(module, fixture.CreateGuestClient());
        await using var renderer = new ComponentRenderer(services, services.GetRequiredService<ILoggerFactory>());

        var (page, componentId) = await renderer.RenderComponentAsync<Join>(Parameters("session-1"));
        await module.WaitForInvocationCountAsync("takeInvitation", 1, cancellationSource.Token);
        await renderer.WaitForRenderCountAsync(2, cancellationSource.Token);

        Assert.Equal(1, module.InvocationCount("takeInvitation"));
        Assert.Equal(0, fixture.ConnectionCount);
        Assert.Empty(fixture.Invocations);
        await renderer.WaitForTextContentAsync(componentId, "This invitation is invalid or has been replaced.", cancellationSource.Token);
        var text = await GetTextContentAsync(renderer, componentId);
        Assert.Contains("This invitation is invalid or has been replaced.", text);
        Assert.Equal(new BrowserReply(false, "not-joined"), await page.StopRun());
    }

    [Fact]
    public async Task ValidInvitationAdmitsTheGuestWithoutRenderingItsOpaqueProof()
    {
        using var cancellationSource = CreateTimeout();
        await using var fixture = await PageControlHubFixture.StartAsync(cancellationSource.Token);
        const string invitation = "private-invitation-proof";
        await using var module = new RecordedJsModule { Invitation = invitation };
        await using var services = CreateServices(module, fixture.CreateGuestClient());
        await using var renderer = new ComponentRenderer(services, services.GetRequiredService<ILoggerFactory>());

        var (_, componentId) = await renderer.RenderComponentAsync<Join>(Parameters("session-1"));
        await fixture.WaitForInvocationAsync("Join", cancellationSource.Token);

        Assert.Equal(["SubscribeGuest", "Join"], fixture.Invocations.Select(invocation => invocation.Method));
        var request = Assert.IsType<GuestJoinRequest>(fixture.Invocations[1].Arguments[0]);
        Assert.Equal(("session-1", invitation), (request.SessionId, request.InvitationProof));
        Assert.DoesNotContain(invitation, await GetTextContentAsync(renderer, componentId), StringComparison.Ordinal);
        Assert.DoesNotContain(invitation, module.Invocations
            .Where(invocation => invocation.Identifier != "takeInvitation")
            .SelectMany(invocation => invocation.Arguments)
            .OfType<string>());
    }

    [Fact]
    public async Task PausedGuestUsesAWarningStatus()
    {
        using var cancellationSource = CreateTimeout();
        await using var fixture = await PageControlHubFixture.StartAsync(cancellationSource.Token);
        await using var module = new RecordedJsModule { Invitation = "proof" };
        await using var services = CreateServices(module, fixture.CreateGuestClient());
        await using var renderer = new ComponentRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var (_, componentId) = await renderer.RenderComponentAsync<Join>(Parameters("session-1"));
        await fixture.WaitForInvocationAsync("Join", cancellationSource.Token);

        await fixture.SendGuestStatusAsync(
            PageControlHubFixture.ReadyGuestStatus(1) with { Mode = "Paused", SessionMode = "Approved" },
            cancellationSource.Token);
        await renderer.WaitForTextContentAsync(componentId, "Paused. Waiting for the operator.", cancellationSource.Token);

        Assert.True(await HasAttributeValueContainingAsync(renderer, componentId, "variant", "warning"));
    }

    [Fact]
    public async Task ManagedDisplayValidationRejectsInvalidPayloadsAndRendersTheLastValidDisplay()
    {
        using var cancellationSource = CreateTimeout();
        await using var fixture = await PageControlHubFixture.StartAsync(cancellationSource.Token);
        await using var module = new RecordedJsModule { Invitation = "proof" };
        await using var services = CreateServices(module, fixture.CreateGuestClient());
        await using var renderer = new ComponentRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var (page, componentId) = await renderer.RenderComponentAsync<Join>(Parameters("session-1"));
        await fixture.WaitForInvocationAsync("Join", cancellationSource.Token);
        await PublishReadyStatusAsync(fixture, module, renderer, cancellationSource.Token);
        await renderer.WaitForRenderCountAsync(2, cancellationSource.Token);

        var display = new GuestDisplayState(
            new ControlValues(0, 40, 20, 30, 50),
            new ControlValues(0, 30, 20, 30, 50),
            true,
            new string('s', 200),
            new string('e', 200));
        var expectedRenderCount = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var expected = renderer.RenderCount + 1;
            await page.UpdateDisplay(display);
            return expected;
        });
        await renderer.WaitForRenderCountAsync(expectedRenderCount, cancellationSource.Token);

        var nullSelected = new GuestDisplayState(null!, null, false, "", null);
        var longStatus = display with { Status = new string('s', 201) };
        var longError = display with { Error = new string('e', 201) };

        await Assert.ThrowsAsync<ArgumentException>(() => renderer.Dispatcher.InvokeAsync(() => page.UpdateDisplay(nullSelected)));
        await Assert.ThrowsAsync<ArgumentException>(() => renderer.Dispatcher.InvokeAsync(() => page.UpdateDisplay(longStatus)));
        await Assert.ThrowsAsync<ArgumentException>(() => renderer.Dispatcher.InvokeAsync(() => page.UpdateDisplay(longError)));

        await renderer.WaitForTextContentAsync(componentId, Assert.IsType<string>(display.Status), cancellationSource.Token);
        await renderer.WaitForTextContentAsync(componentId, Assert.IsType<string>(display.Error), cancellationSource.Token);
        var text = await GetTextContentAsync(renderer, componentId);
        Assert.Contains(Assert.IsType<string>(display.Status), text, StringComparison.Ordinal);
        Assert.Contains(Assert.IsType<string>(display.Error), text, StringComparison.Ordinal);
        Assert.Equal(["SubscribeGuest", "Join"], fixture.Invocations.Select(invocation => invocation.Method));
    }

    [Fact]
    public async Task ManagedInputBoundsFailClosedBeforeTheClientAndExactBoundariesReachTheJoinedGuest()
    {
        using var cancellationSource = CreateTimeout();
        await using var fixture = await PageControlHubFixture.StartAsync(cancellationSource.Token);
        await using var module = new RecordedJsModule { Invitation = "proof" };
        await using var services = CreateServices(module, fixture.CreateGuestClient());
        await using var renderer = new ComponentRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var (page, _) = await renderer.RenderComponentAsync<Join>(Parameters("session-1"));
        await fixture.WaitForInvocationAsync("Join", cancellationSource.Token);

        var notReady = await page.SubmitRun(new(2, "epoch-1", 7, "grant-1", 1, true, ValidValues()));
        Assert.Equal(new BrowserReply(false, "not-ready"), notReady);
        Assert.Equal(["SubscribeGuest", "Join"], fixture.Invocations.Select(invocation => invocation.Method));

        await PublishReadyStatusAsync(fixture, module, renderer, cancellationSource.Token);
        var invalidInputs = new (GuestInput Input, string Code)[]
        {
            (ValidInput() with { V = 1 }, "not-ready"),
            (ValidInput() with { Run = false }, "not-ready"),
            (ValidInput() with { Epoch = "wrong-epoch" }, "not-ready"),
            (ValidInput() with { PolicyVersion = 8 }, "policy-invalid"),
            (ValidInput() with { Desired = ValidValues() with { Pattern = 1 } }, "invalid-input"),
            (ValidInput() with { Desired = ValidValues() with { Sensation = 49 } }, "invalid-input"),
            (ValidInput() with { Desired = ValidValues() with { Speed = -1 } }, "invalid-input"),
            (ValidInput() with { Desired = ValidValues() with { Speed = 81 } }, "invalid-input"),
            (ValidInput() with { Desired = ValidValues() with { Stroke = 0 } }, "invalid-input"),
            (ValidInput() with { Desired = ValidValues() with { Stroke = 61 } }, "invalid-input"),
            (ValidInput() with { Desired = ValidValues() with { Depth = 0 } }, "invalid-input"),
            (ValidInput() with { Desired = ValidValues() with { Depth = 61 } }, "invalid-input")
        };

        foreach (var (input, code) in invalidInputs)
            Assert.Equal(new BrowserReply(false, code), await page.SubmitRun(input));

        Assert.Equal(["SubscribeGuest", "Join"], fixture.Invocations.Select(invocation => invocation.Method));

        var accepted = await page.SubmitRun(ValidInput());
        var stopped = await page.StopRun();

        Assert.Equal("accepted-Submit", accepted.Code);
        Assert.True(accepted.Ok);
        Assert.Equal("accepted-GuestStop", stopped.Code);
        Assert.True(stopped.Ok);
        Assert.Equal(["SubscribeGuest", "Join", "Submit", "GuestStop"],
            fixture.Invocations.Select(invocation => invocation.Method));
        var submitted = Assert.IsType<GuestInputRequest>(fixture.Invocations[2].Arguments[0]);
        Assert.Equal(ValidValues(), submitted.Input.Desired);
    }

    [Fact]
    public async Task ClientRejectionsAndUnknownOutcomesMapToDistinctBrowserRepliesWithoutRetrying()
    {
        using var cancellationSource = CreateTimeout();
        await using var fixture = await PageControlHubFixture.StartAsync(cancellationSource.Token);
        await using var module = new RecordedJsModule { Invitation = "proof" };
        await using var services = CreateServices(module, fixture.CreateGuestClient(commandTimeoutSeconds: 1));
        await using var renderer = new ComponentRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var (page, _) = await renderer.RenderComponentAsync<Join>(Parameters("session-1"));
        await fixture.WaitForInvocationAsync("Join", cancellationSource.Token);
        await PublishReadyStatusAsync(fixture, module, renderer, cancellationSource.Token);

        fixture.Reject("Submit", "fixture-rejection");
        var rejection = await page.SubmitRun(ValidInput());

        Assert.False(rejection.Ok);
        Assert.Equal("fixture-rejection", rejection.Code);
        Assert.Equal(1, fixture.Invocations.Count(invocation => invocation.Method == "Submit"));

        fixture.Hold("Submit");
        try
        {
            var pending = page.SubmitRun(ValidInput() with { Seq = 2 });
            await fixture.WaitForInvocationAsync("Submit", cancellationSource.Token);
            var unknown = await pending;

            Assert.Equal(new BrowserReply(false, "outcome-unknown"), unknown);
            Assert.Equal(2, fixture.Invocations.Count(invocation => invocation.Method == "Submit"));
        }
        finally
        {
            fixture.Release("Submit");
        }
    }

    [Fact]
    public async Task StaleGuestSnapshotsCannotRegressTheVisibleControlStateButNewerSnapshotsCan()
    {
        using var cancellationSource = CreateTimeout();
        await using var fixture = await PageControlHubFixture.StartAsync(cancellationSource.Token);
        await using var module = new RecordedJsModule { Invitation = "proof" };
        await using var services = CreateServices(module, fixture.CreateGuestClient());
        await using var renderer = new ComponentRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var (_, componentId) = await renderer.RenderComponentAsync<Join>(Parameters("session-1"));
        await fixture.WaitForInvocationAsync("Join", cancellationSource.Token);

        await PublishReadyStatusAsync(fixture, module, renderer, cancellationSource.Token, 2);
        var updateCount = module.InvocationCount("updateAuthority");
        var renderCount = renderer.RenderCount;
        await fixture.SendGuestStatusAsync(PageControlHubFixture.LockedGuestStatus(1), cancellationSource.Token);
        await fixture.SendGuestStatusAsync(PageControlHubFixture.LockedGuestStatus(3), cancellationSource.Token);
        await module.WaitForInvocationCountAsync("updateAuthority", updateCount + 1, cancellationSource.Token);
        await renderer.WaitForRenderCountAsync(renderCount + 1, cancellationSource.Token);

        Assert.Equal(updateCount + 1, module.InvocationCount("updateAuthority"));
        await renderer.WaitForTextContentAsync(componentId, "Locked by the operator. Sending is disabled.", cancellationSource.Token);
        Assert.Contains("Locked by the operator. Sending is disabled.", await GetTextContentAsync(renderer, componentId));
        Assert.Equal(["SubscribeGuest", "Join"], fixture.Invocations.Select(invocation => invocation.Method));
    }

    [Fact]
    public async Task ResyncLossDiscardsGuestAuthorityAndPreventsLaterBrowserCommands()
    {
        using var cancellationSource = CreateTimeout();
        await using var fixture = await PageControlHubFixture.StartAsync(cancellationSource.Token);
        await using var module = new RecordedJsModule { Invitation = "proof" };
        await using var services = CreateServices(module, fixture.CreateGuestClient());
        await using var renderer = new ComponentRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var (page, componentId) = await renderer.RenderComponentAsync<Join>(Parameters("session-1"));
        await fixture.WaitForInvocationAsync("Join", cancellationSource.Token);
        await PublishReadyStatusAsync(fixture, module, renderer, cancellationSource.Token);

        var invocationCount = fixture.Invocations.Count;
        var renderCount = renderer.RenderCount;
        await fixture.SendResyncAsync("fixture-loss", cancellationSource.Token);
        await module.WaitForInvocationCountAsync("disconnect", 1, cancellationSource.Token);
        await fixture.WaitForDisconnectionAsync(cancellationSource.Token);
        await renderer.WaitForRenderCountAsync(renderCount + 1, cancellationSource.Token);

        var submit = await page.SubmitRun(ValidInput());
        var stop = await page.StopRun();

        Assert.Equal(new BrowserReply(false, "not-ready"), submit);
        Assert.Equal(new BrowserReply(false, "not-joined"), stop);
        Assert.Equal(invocationCount, fixture.Invocations.Count);
        Assert.Equal("danger", renderer.GetElementAttributeValue(
            componentId, "rw-alert", "data-testid", "guest-disconnected", "variant"));
        Assert.Null(renderer.GetElementAttributeValue(
            componentId, "rw-alert", "data-testid", "guest-disconnected", "hidden"));
        Assert.True(renderer.HasAttributeValueContaining(componentId, "data-connection-lost", "true"));
        Assert.DoesNotContain("Session ended.", await GetTextContentAsync(renderer, componentId));
    }

    [Fact]
    public async Task DeliveryResyncEndsGuestWithoutClaimingBrowserConnectionLoss()
    {
        using var cancellationSource = CreateTimeout();
        await using var fixture = await PageControlHubFixture.StartAsync(cancellationSource.Token);
        await using var module = new RecordedJsModule { Invitation = "proof" };
        await using var services = CreateServices(module, fixture.CreateGuestClient());
        await using var renderer = new ComponentRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var (page, componentId) = await renderer.RenderComponentAsync<Join>(Parameters("session-1"));
        await fixture.WaitForInvocationAsync("Join", cancellationSource.Token);
        await PublishReadyStatusAsync(fixture, module, renderer, cancellationSource.Token);

        Assert.Equal(0, fixture.DisconnectionCount);
        using var disconnectDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationSource.Token);
        disconnectDeadline.CancelAfter(TimeSpan.FromSeconds(2));
        await fixture.SendResyncAsync("delivery-resync-required", cancellationSource.Token);

        await fixture.WaitForDisconnectionAsync(disconnectDeadline.Token);
        await renderer.WaitForTextContentAsync(componentId, "Session ended. Open the invite link again to rejoin.", cancellationSource.Token);
        Assert.Equal(1, fixture.DisconnectionCount);
        Assert.Equal(0, module.InvocationCount("disconnect"));
        Assert.Equal(new BrowserReply(false, "not-joined"), await page.StopRun());
    }

    private static ServiceProvider CreateServices(RecordedJsModule module, GuestControlClient client) =>
        new ServiceCollection()
            .AddLogging()
            .AddSingleton<IJSRuntime>(new RecordedJsRuntime(module))
            .AddSingleton(client)
            .AddSingleton(ResourceAssetCollection.Empty)
            .BuildServiceProvider();

    private static ParameterView Parameters(string sessionId) =>
        ParameterView.FromDictionary(new Dictionary<string, object?>
        {
            [nameof(Join.SessionId)] = sessionId
        });

    private static Task<string> GetTextContentAsync(ComponentRenderer renderer, int componentId) =>
        renderer.Dispatcher.InvokeAsync(() => renderer.GetTextContent(componentId));

    private static Task<bool> HasAttributeValueContainingAsync(
        ComponentRenderer renderer,
        int componentId,
        string attributeName,
        string value) =>
        renderer.Dispatcher.InvokeAsync(() =>
            renderer.HasAttributeValueContaining(componentId, attributeName, value));

    private static async Task PublishReadyStatusAsync(
        PageControlHubFixture fixture,
        RecordedJsModule module,
        ComponentRenderer renderer,
        CancellationToken cancellationToken,
        long snapshotRevision = 1)
    {
        await module.WaitForInvocationCountAsync("attach", 1, cancellationToken);
        await module.WaitForInvocationCountAsync("updateAuthority", 1, cancellationToken);
        var expectedRenders = renderer.RenderCount + 1;
        var expectedUpdates = module.InvocationCount("updateAuthority") + 1;
        await fixture.SendGuestStatusAsync(
            PageControlHubFixture.ReadyGuestStatus(snapshotRevision),
            cancellationToken);
        await module.WaitForInvocationCountAsync("updateAuthority", expectedUpdates, cancellationToken);
        await renderer.WaitForRenderCountAsync(expectedRenders, cancellationToken);
    }

    private static GuestInput ValidInput() => new(2, "epoch-1", 7, "grant-1", 1, true, ValidValues());

    private static ControlValues ValidValues() => new(0, 80, 60, 60, 50);

    private static CancellationTokenSource CreateTimeout()
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        source.CancelAfter(TimeSpan.FromSeconds(10));
        return source;
    }
}
