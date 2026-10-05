using System.Reflection;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace OssmSharp.Web.Api.Tests;

#pragma warning disable BL0006 // Test-only lifecycle dispatch; browser tests own real rendering.

internal sealed class StateOnlyRenderer : Renderer
{
    private readonly ComponentBase _component;
    private TaskCompletionSource? _nextDisplay;

    internal StateOnlyRenderer(ComponentBase component)
        : base(new ServiceCollection().BuildServiceProvider(), NullLoggerFactory.Instance)
    {
        _component = component;
        AssignRootComponentId(component);
    }

    public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();

    internal Task RunAsync(Func<Task> action) => Dispatcher.InvokeAsync(action);

    internal async Task ObserveChangeAsync(Action action)
    {
        _nextDisplay = new(TaskCreationOptions.RunContinuationsAsynchronously);
        action();
        await _nextDisplay.Task.WaitAsync(TimeSpan.FromSeconds(5), Xunit.TestContext.Current.CancellationToken);
    }

    protected override void ProcessPendingRender()
    {
        // State tests invoke lifecycle explicitly, without rendering child components.
        typeof(ComponentBase).GetField("_hasPendingQueuedRender", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(_component, false);
        _nextDisplay?.TrySetResult();
    }

    protected override Task UpdateDisplayAsync(in RenderBatch renderBatch) => Task.CompletedTask;

    protected override void HandleException(Exception exception) => throw exception;
}
