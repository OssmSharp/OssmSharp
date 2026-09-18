using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using OSSMDotNet.AdminWeb.Components.Pages;
using OSSMDotNet.ControlClient;
using OSSMDotNet.GuestWeb.Tests;
using OSSMDotNet.Models;

namespace OSSMDotNet.AdminWeb.Tests;

public sealed class HomePageBehaviorTests
{
    [Fact]
    public async Task PendingGuestShowsTheWarningNoticeUntilTheSessionIsApproved()
    {
        using var cancellationSource = CreateTimeout();
        await using var fixture = await PageControlHubFixture.StartAsync(cancellationSource.Token);
        var pending = new AuthorityPrecondition("instance-1", 10, new GuestAdmission("session-1", "guest-1", "PendingApproval"));
        fixture.OperatorStatus = PageControlHubFixture.OperatorStatusFor(pending);
        await using var module = new RecordedJsModule();
        await using var services = CreateServices(module, fixture.CreateOperatorClient());
        await using var renderer = new ComponentRenderer(services, services.GetRequiredService<ILoggerFactory>());

        var (_, componentId) = await renderer.RenderComponentAsync<Home>(ParameterView.Empty);
        await module.WaitForInvocationCountAsync("attachLog", 1, cancellationSource.Token);

        Assert.Contains("A guest is waiting. Click Allow control to enable control.", renderer.GetTextContent(componentId));
        Assert.Equal("warning", renderer.GetElementAttributeValue(
            componentId, "rw-alert", "data-testid", "pending-guest-notice", "variant"));
        Assert.True(renderer.HasAttributeValueContaining(componentId, "class", "rw-inline-notification"));
        Assert.False(renderer.HasAttributeValueContaining(componentId, "class", "rw-notice"));
        Assert.True(renderer.HasAttributeValueContaining(componentId, "class", "rw-attention"));

        var renderCount = renderer.RenderCount;
        await fixture.SendOperatorStateAsync(
            PageControlHubFixture.OperatorStatusFor(
                pending with
                {
                    AuthorityRevision = 11,
                    GuestAdmission = pending.GuestAdmission with { SessionMode = "Approved" }
                },
                sessionMode: "Approved"),
            cancellationSource.Token);
        await renderer.WaitForRenderCountAsync(renderCount + 1, cancellationSource.Token);

        Assert.DoesNotContain("A guest is waiting. Click Allow control to enable control.", renderer.GetTextContent(componentId));
        Assert.Null(renderer.GetElementAttributeValue(
            componentId, "rw-alert", "data-testid", "pending-guest-notice", "variant"));
        Assert.False(renderer.HasAttributeValueContaining(componentId, "class", "rw-attention"));
    }

    [Fact]
    public async Task PendingGuestDoesNotShowTheAllowControlNoticeWhenTheControllerIsLocked()
    {
        using var cancellationSource = CreateTimeout();
        await using var fixture = await PageControlHubFixture.StartAsync(cancellationSource.Token);
        var pending = new AuthorityPrecondition("instance-1", 10, new GuestAdmission("session-1", "guest-1", "PendingApproval"));
        var status = PageControlHubFixture.OperatorStatusFor(pending);
        fixture.OperatorStatus = status with
        {
            Authority = status.Authority with
            {
                Snapshot = status.Authority.Snapshot with { Mode = "Locked" }
            }
        };
        await using var module = new RecordedJsModule();
        await using var services = CreateServices(module, fixture.CreateOperatorClient());
        await using var renderer = new ComponentRenderer(services, services.GetRequiredService<ILoggerFactory>());

        var (_, componentId) = await renderer.RenderComponentAsync<Home>(ParameterView.Empty);
        await module.WaitForInvocationCountAsync("attachLog", 1, cancellationSource.Token);

        Assert.Contains("Guest waiting", renderer.GetTextContent(componentId));
        Assert.Contains("Unlock", renderer.GetTextContent(componentId));
        Assert.DoesNotContain("A guest is waiting. Click Allow control to enable control.", renderer.GetTextContent(componentId));
        Assert.False(renderer.HasAttributeValueContaining(componentId, "class", "rw-attention"));
    }

    [Fact]
    public async Task InitialSubscriptionRendersThePendingGuestStateAndAttachesTheCurrentActivityLog()
    {
        using var cancellationSource = CreateTimeout();
        await using var fixture = await PageControlHubFixture.StartAsync(cancellationSource.Token);
        var expected = new AuthorityPrecondition("instance-1", 11, new GuestAdmission("session-1", "guest-1", "PendingApproval"));
        fixture.OperatorStatus = PageControlHubFixture.OperatorStatusFor(expected);
        await using var module = new RecordedJsModule();
        await using var services = CreateServices(module, fixture.CreateOperatorClient());
        await using var renderer = new ComponentRenderer(services, services.GetRequiredService<ILoggerFactory>());

        var (_, componentId) = await renderer.RenderComponentAsync<Home>(ParameterView.Empty);
        await module.WaitForInvocationCountAsync("attachLog", 1, cancellationSource.Token);

        var subscription = Assert.Single(fixture.Invocations);
        Assert.Equal("SubscribeOperator", subscription.Method);
        Assert.Equal(0L, Assert.IsType<long>(subscription.Arguments[1]));
        Assert.Contains("Guest waiting", renderer.GetTextContent(componentId));
        Assert.True(renderer.HasAttributeValueContaining(componentId, "class", "rw-attention"));
        Assert.Contains("Allow control", renderer.GetTextContent(componentId));
    }

    [Fact]
    public async Task SimulatorIndicatorAppearsInThePageHeader()
    {
        using var cancellationSource = CreateTimeout();
        await using var fixture = await PageControlHubFixture.StartAsync(cancellationSource.Token);
        var expected = new AuthorityPrecondition("instance-1", 12, new GuestAdmission(null, null, "None"));
        fixture.OperatorStatus = PageControlHubFixture.OperatorStatusFor(expected);
        await using var module = new RecordedJsModule();
        await using var services = CreateServices(module, fixture.CreateOperatorClient());
        await using var renderer = new ComponentRenderer(services, services.GetRequiredService<ILoggerFactory>());

        var (_, componentId) = await renderer.RenderComponentAsync<Home>(ParameterView.Empty);
        await module.WaitForInvocationCountAsync("attachLog", 1, cancellationSource.Token);

        Assert.Equal("operator-page-header-status", renderer.GetElementAttributeValue(
            componentId, "div", "data-testid", "operator-page-header-status", "class"));
        Assert.Equal("info", renderer.GetElementAttributeValue(
            componentId, "rw-badge", "data-testid", "simulator-mode", "variant"));
    }

    [Fact]
    public async Task PendingDeviceOperationUsesAnAccessibleStatusMarker()
    {
        using var cancellationSource = CreateTimeout();
        await using var fixture = await PageControlHubFixture.StartAsync(cancellationSource.Token);
        var expected = new AuthorityPrecondition("instance-1", 12, new GuestAdmission(null, null, "None"));
        var initial = PageControlHubFixture.OperatorStatusFor(expected, snapshotRevision: 1);
        fixture.OperatorStatus = initial;
        await using var module = new RecordedJsModule();
        await using var services = CreateServices(module, fixture.CreateOperatorClient());
        await using var renderer = new ComponentRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var (_, componentId) = await renderer.RenderComponentAsync<Home>(ParameterView.Empty);
        await module.WaitForInvocationCountAsync("attachLog", 1, cancellationSource.Token);

        var pending = initial with
        {
            Authority = initial.Authority with
            {
                Snapshot = initial.Authority.Snapshot with { OperationPending = true },
                SnapshotRevision = 2
            }
        };
        var renderCount = renderer.RenderCount;
        await fixture.SendOperatorStateAsync(pending, cancellationSource.Token);
        await renderer.WaitForRenderCountAsync(renderCount + 1, cancellationSource.Token);

        Assert.Equal("img", renderer.GetElementAttributeValue(
            componentId, "span", "data-testid", "operator-operation-status", "role"));
        Assert.Equal("Waiting for the device to settle.", renderer.GetElementAttributeValue(
            componentId, "span", "data-testid", "operator-operation-status", "aria-label"));
        Assert.Equal("Waiting for the device to settle.", renderer.GetElementAttributeValue(
            componentId, "span", "data-testid", "operator-operation-status", "title"));
        Assert.Equal("operator-operation-status", renderer.GetElementAttributeValue(
            componentId, "span", "data-testid", "operator-operation-status", "class"));
        Assert.DoesNotContain("Waiting for the device to settle.", renderer.GetTextContent(componentId));
    }

    [Fact]
    public async Task ApplyingLimitsUsesTheLatestAuthorityPreconditionAndReportsTheCurrentSuccess()
    {
        using var cancellationSource = CreateTimeout();
        await using var fixture = await PageControlHubFixture.StartAsync(cancellationSource.Token);
        var expected = new AuthorityPrecondition("instance-1", 12, new GuestAdmission("session-1", "guest-1", "Approved"));
        fixture.OperatorStatus = PageControlHubFixture.OperatorStatusFor(expected, 80, 60, "Approved");
        await using var module = new RecordedJsModule();
        await using var services = CreateServices(module, fixture.CreateOperatorClient());
        await using var renderer = new ComponentRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var (_, componentId) = await renderer.RenderComponentAsync<Home>(ParameterView.Empty);
        await module.WaitForInvocationCountAsync("attachLog", 1, cancellationSource.Token);

        var apply = renderer.FindEventHandlerIdForElementText(componentId, "button", "Apply limits", "onclick");
        var renderCount = renderer.RenderCount;
        await renderer.DispatchEventAsync(apply, new MouseEventArgs());
        await fixture.WaitForInvocationAsync("SetLimits", cancellationSource.Token);
        await renderer.WaitForRenderCountAsync(renderCount + 1, cancellationSource.Token);

        Assert.Equal(["SubscribeOperator", "SetLimits"], fixture.Invocations.Select(invocation => invocation.Method));
        var setLimits = fixture.Invocations[1];
        Assert.Equal(new ControlLimits(80, 60), Assert.IsType<ControlLimits>(setLimits.Arguments[0]));
        Assert.Equal(expected, Assert.IsType<AuthorityPrecondition>(setLimits.Arguments[1]));
        Assert.Matches("^[0-9a-f]{32}$", Assert.IsType<string>(setLimits.Arguments[2]));
        Assert.Contains("Limits applied.", renderer.GetTextContent(componentId));
    }

    [Fact]
    public async Task StaleLimitRejectionShowsTheCurrentSafeNoticeWithoutAnotherOperatorAction()
    {
        using var cancellationSource = CreateTimeout();
        await using var fixture = await PageControlHubFixture.StartAsync(cancellationSource.Token);
        var expected = new AuthorityPrecondition("instance-1", 13, new GuestAdmission("session-1", "guest-1", "Approved"));
        fixture.OperatorStatus = PageControlHubFixture.OperatorStatusFor(expected, 80, 60, "Approved");
        fixture.Reject("SetLimits", "authority-revision-mismatch");
        await using var module = new RecordedJsModule();
        await using var services = CreateServices(module, fixture.CreateOperatorClient());
        await using var renderer = new ComponentRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var (_, componentId) = await renderer.RenderComponentAsync<Home>(ParameterView.Empty);
        await module.WaitForInvocationCountAsync("attachLog", 1, cancellationSource.Token);

        var apply = renderer.FindEventHandlerIdForElementText(componentId, "button", "Apply limits", "onclick");
        var renderCount = renderer.RenderCount;
        await renderer.DispatchEventAsync(apply, new MouseEventArgs());
        await fixture.WaitForInvocationAsync("SetLimits", cancellationSource.Token);
        await renderer.WaitForRenderCountAsync(renderCount + 1, cancellationSource.Token);

        Assert.Equal(["SubscribeOperator", "SetLimits"], fixture.Invocations.Select(invocation => invocation.Method));
        await renderer.WaitForTextContentAsync(
            componentId,
            "The control state changed. Wait for the latest update, then try again.",
            cancellationSource.Token);
        Assert.Contains("The control state changed. Wait for the latest update, then try again.",
            renderer.GetTextContent(componentId));
        Assert.DoesNotContain("Limits applied.", renderer.GetTextContent(componentId));
    }

    [Fact]
    public async Task DraftLimitRemainsVisibleAcrossAnIncomingControlState()
    {
        using var cancellationSource = CreateTimeout();
        await using var fixture = await PageControlHubFixture.StartAsync(cancellationSource.Token);
        var precondition = new AuthorityPrecondition("instance-1", 14, new GuestAdmission("session-1", "guest-1", "Approved"));
        fixture.OperatorStatus = PageControlHubFixture.OperatorStatusFor(precondition, 80, 60, "Approved");
        await using var module = new RecordedJsModule();
        await using var services = CreateServices(module, fixture.CreateOperatorClient());
        await using var renderer = new ComponentRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var (_, componentId) = await renderer.RenderComponentAsync<Home>(ParameterView.Empty);
        await module.WaitForInvocationCountAsync("attachLog", 1, cancellationSource.Token);

        var speedInput = renderer.FindEventHandlerIdForElementAttribute(
            componentId,
            "input",
            "id",
            "operator-speed-cap",
            "oninput",
            "onchange");
        var inputRenderCount = renderer.RenderCount;
        await renderer.DispatchEventAsync(speedInput, new ChangeEventArgs { Value = "55" });
        await renderer.WaitForRenderCountAsync(inputRenderCount + 1, cancellationSource.Token);

        Assert.Equal("55", renderer.GetElementAttributeValue(
            componentId, "input", "id", "operator-speed-cap", "value"));
        Assert.Contains("Not applied", renderer.GetTextContent(componentId));

        var statusRenderCount = renderer.RenderCount;
        await fixture.SendOperatorStateAsync(
            PageControlHubFixture.OperatorStatusFor(precondition with { AuthorityRevision = 15 }, 70, 50, "Approved", 2),
            cancellationSource.Token);
        await renderer.WaitForRenderCountAsync(statusRenderCount + 1, cancellationSource.Token);

        Assert.Equal("55", renderer.GetElementAttributeValue(
            componentId, "input", "id", "operator-speed-cap", "value"));
        Assert.Contains("Not applied", renderer.GetTextContent(componentId));
        Assert.Equal(["SubscribeOperator"], fixture.Invocations.Select(invocation => invocation.Method));
    }

    [Fact]
    public async Task ActivityGapAndRetentionKeepOnlyTheCurrentBoundedLogAndFollowItInTheBrowser()
    {
        using var cancellationSource = CreateTimeout();
        await using var fixture = await PageControlHubFixture.StartAsync(cancellationSource.Token);
        var precondition = new AuthorityPrecondition("instance-1", 16, new GuestAdmission(null, null, "None"));
        fixture.OperatorStatus = PageControlHubFixture.OperatorStatusFor(precondition, sessionMode: "None");
        await using var module = new RecordedJsModule();
        await using var services = CreateServices(module, fixture.CreateOperatorClient());
        await using var renderer = new ComponentRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var (_, componentId) = await renderer.RenderComponentAsync<Home>(ParameterView.Empty);
        await module.WaitForInvocationCountAsync("attachLog", 1, cancellationSource.Token);

        await SendLogAndWaitAsync(renderer, fixture,
            new ControlActivityBatch(1, 1, 1, 1, null, false,
                [new ControlActivityEntry(1, DateTimeOffset.UnixEpoch, "Information", "before-gap")]),
            cancellationSource.Token);
        await SendLogAndWaitAsync(renderer, fixture,
            new ControlActivityBatch(2, 2, 2, 2, null, true,
                [new ControlActivityEntry(2, DateTimeOffset.UnixEpoch, "Warning", "after-gap")]),
            cancellationSource.Token);
        var entries = Enumerable.Range(0, 201)
            .Select(index => new ControlActivityEntry(index + 3, DateTimeOffset.UnixEpoch, "Information", $"entry-{index}"))
            .ToArray();
        await SendLogAndWaitAsync(renderer, fixture,
            new ControlActivityBatch(3, 203, 203, 203, null, false, entries),
            cancellationSource.Token);

        var text = renderer.GetTextContent(componentId);
        Assert.DoesNotContain("before-gap", text, StringComparison.Ordinal);
        Assert.DoesNotContain("after-gap", text, StringComparison.Ordinal);
        Assert.DoesNotContain("entry-0", text, StringComparison.Ordinal);
        Assert.Contains("entry-1", text, StringComparison.Ordinal);
        Assert.Contains("entry-200", text, StringComparison.Ordinal);
        Assert.Equal(200, renderer.CountAttributeValuesContaining(componentId, "class", "log-entry"));
        Assert.Equal(1, module.InvocationCount("attachLog"));
    }

    [Fact]
    public async Task ResyncLossDetachesTheLogAndShowsTheCurrentConnectionNotice()
    {
        using var cancellationSource = CreateTimeout();
        await using var fixture = await PageControlHubFixture.StartAsync(cancellationSource.Token);
        var precondition = new AuthorityPrecondition("instance-1", 17, new GuestAdmission(null, null, "None"));
        fixture.OperatorStatus = PageControlHubFixture.OperatorStatusFor(precondition, sessionMode: "None");
        await using var module = new RecordedJsModule();
        await using var services = CreateServices(module, fixture.CreateOperatorClient());
        await using var renderer = new ComponentRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var (_, componentId) = await renderer.RenderComponentAsync<Home>(ParameterView.Empty);
        await module.WaitForInvocationCountAsync("attachLog", 1, cancellationSource.Token);

        await fixture.SendResyncAsync("fixture-loss", cancellationSource.Token);
        await module.WaitForInvocationCountAsync("detachLog", 1, cancellationSource.Token);

        await renderer.WaitForTextContentAsync(
            componentId,
            "The control-service connection closed. Reload the operator page.",
            cancellationSource.Token);
        Assert.Contains("The control-service connection closed. Reload the operator page.",
            renderer.GetTextContent(componentId));
        Assert.DoesNotContain("Operator actions", renderer.GetTextContent(componentId), StringComparison.Ordinal);
        Assert.Equal(["SubscribeOperator"], fixture.Invocations.Select(invocation => invocation.Method));
    }

    [Fact]
    public async Task CopyUsesTheInvitationUrlAndKeepsThePageSuccessStateInSyncWithTheButton()
    {
        using var cancellationSource = CreateTimeout();
        await using var fixture = await PageControlHubFixture.StartAsync(cancellationSource.Token);
        const string invitationUrl = "http://127.0.0.1:5182/join/fixture#invite=public-link";
        var precondition = new AuthorityPrecondition("instance-1", 18, new GuestAdmission(null, null, "None"));
        fixture.OperatorStatus = PageControlHubFixture.OperatorStatusFor(precondition, sessionMode: "None", invitationUrl: invitationUrl);
        await using var module = new RecordedJsModule { CopyResult = true };
        await using var services = CreateServices(module, fixture.CreateOperatorClient());
        await using var renderer = new ComponentRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var (_, componentId) = await renderer.RenderComponentAsync<Home>(ParameterView.Empty);
        await module.WaitForInvocationCountAsync("attachLog", 1, cancellationSource.Token);

        var copy = renderer.FindEventHandlerIdForElementText(componentId, "button", "Copy", "onclick");
        var renderCount = renderer.RenderCount;
        await renderer.DispatchEventAsync(copy, new MouseEventArgs());
        await module.WaitForInvocationCountAsync("copy", 1, cancellationSource.Token);
        await renderer.WaitForRenderCountAsync(renderCount + 1, cancellationSource.Token);

        var copied = Assert.Single(module.Invocations, invocation => invocation.Identifier == "copy");
        Assert.Equal(invitationUrl, Assert.IsType<string>(copied.Arguments[0]));
        Assert.Contains("Copied", renderer.GetTextContent(componentId));
        Assert.True(renderer.HasAttributeValueContaining(componentId, "aria-label", "Copied"));
    }

    private static ServiceProvider CreateServices(RecordedJsModule module, OperatorControlClient client) =>
        new ServiceCollection()
            .AddLogging()
            .AddSingleton<TimeProvider>(TimeProvider.System)
            .AddSingleton<IJSRuntime>(new RecordedJsRuntime(module))
            .AddSingleton(client)
            .AddSingleton(ResourceAssetCollection.Empty)
            .BuildServiceProvider();

    private static async Task SendLogAndWaitAsync(
        ComponentRenderer renderer,
        PageControlHubFixture fixture,
        ControlActivityBatch batch,
        CancellationToken cancellationToken)
    {
        var renderCount = renderer.RenderCount;
        await fixture.SendOperatorLogAsync(batch, cancellationToken);
        await renderer.WaitForRenderCountAsync(renderCount + 1, cancellationToken);
    }

    private static CancellationTokenSource CreateTimeout()
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        source.CancelAfter(TimeSpan.FromSeconds(10));
        return source;
    }
}
