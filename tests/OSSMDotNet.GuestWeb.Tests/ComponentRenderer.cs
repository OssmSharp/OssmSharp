using System.Diagnostics.CodeAnalysis;

namespace OSSMDotNet.GuestWeb.Tests;

#pragma warning disable BL0006 // Test-only renderer intentionally inspects Blazor render-tree frames.
internal sealed class ComponentRenderer(IServiceProvider services, ILoggerFactory loggerFactory) : Renderer(services, loggerFactory)
{
    public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();

    protected override RendererInfo RendererInfo { get; } = new("Test", isInteractive: true);

    private readonly object _renderGate = new();
    private readonly List<(int Expected, TaskCompletionSource Signal)> _renderWaiters = [];
    private readonly List<(int ComponentId, string ExpectedText, TaskCompletionSource Signal)> _textWaiters = [];
    private int _renderCount;

    internal int RenderCount => Volatile.Read(ref _renderCount);

    internal bool InvokeAfterRender { get; init; } = true;

    internal Task<(TComponent Component, int ComponentId)> RenderComponentAsync<TComponent>(
        ParameterView parameters)
        where TComponent : IComponent
        => Dispatcher.InvokeAsync(async () =>
        {
            var component = (TComponent)InstantiateComponent(typeof(TComponent));
            var componentId = AssignRootComponentId(component);
            await RenderRootComponentAsync(componentId, parameters);
            return (component, componentId);
        });

    internal IReadOnlyList<RenderTreeFrame> GetFrames(int componentId)
    {
        var frames = GetCurrentRenderTreeFrames(componentId);
        var snapshot = new RenderTreeFrame[frames.Count];
        Array.Copy(frames.Array, snapshot, frames.Count);
        return snapshot;
    }

    internal string GetTextContent(int componentId) =>
        string.Concat(ComponentIds(componentId)
            .SelectMany(GetFrames)
            .Where(static frame => frame.FrameType == RenderTreeFrameType.Text)
            .Select(static frame => frame.TextContent));

    internal bool HasAttributeValueContaining(int componentId, string attributeName, string value) =>
        ComponentIds(componentId)
            .SelectMany(GetFrames)
            .Any(frame => frame.FrameType == RenderTreeFrameType.Attribute &&
                string.Equals(frame.AttributeName, attributeName, StringComparison.Ordinal) &&
                frame.AttributeValue?.ToString()?.Contains(value, StringComparison.Ordinal) == true);

    internal int CountAttributeValuesContaining(int componentId, string attributeName, string value) =>
        ComponentIds(componentId)
            .SelectMany(GetFrames)
            .Count(frame => frame.FrameType == RenderTreeFrameType.Attribute &&
                string.Equals(frame.AttributeName, attributeName, StringComparison.Ordinal) &&
                frame.AttributeValue?.ToString()?.Contains(value, StringComparison.Ordinal) == true);

    internal string? GetElementAttributeValue(
        int componentId,
        string elementName,
        string matchAttributeName,
        string matchAttributeValue,
        string valueAttributeName)
    {
        foreach (var frames in ComponentIds(componentId).Select(GetFrames))
        {
            for (var index = 0; index < frames.Count; index++)
            {
                var frame = frames[index];
                if (frame.FrameType != RenderTreeFrameType.Element ||
                    !string.Equals(frame.ElementName, elementName, StringComparison.Ordinal))
                {
                    continue;
                }

                var end = Math.Min(index + frame.ElementSubtreeLength, frames.Count);
                if (!ContainsAttribute(frames, index, end, matchAttributeName, matchAttributeValue))
                    continue;

                return FindAttributeValue(frames, index, end, valueAttributeName);
            }
        }

        return null;
    }

    internal ulong FindEventHandlerIdForElementText(
        int componentId,
        string elementName,
        string text,
        params string[] eventAttributeNames)
    {
        foreach (var frames in ComponentIds(componentId).Select(GetFrames))
        {
            for (var index = 0; index < frames.Count; index++)
            {
                var frame = frames[index];
                if (frame.FrameType != RenderTreeFrameType.Element ||
                    !string.Equals(frame.ElementName, elementName, StringComparison.Ordinal))
                {
                    continue;
                }

                var end = Math.Min(index + frame.ElementSubtreeLength, frames.Count);
                if (!frames.Skip(index).Take(end - index).Any(candidate =>
                    candidate.FrameType == RenderTreeFrameType.Text &&
                    string.Equals(candidate.TextContent, text, StringComparison.Ordinal)))
                {
                    continue;
                }

                var handlerId = FindEventHandlerId(frames, index, end, eventAttributeNames);
                if (handlerId != 0)
                    return handlerId;
            }
        }

        throw new InvalidOperationException($"Could not find the {elementName} element for '{text}'.");
    }

    internal ulong FindEventHandlerIdForElementAttribute(
        int componentId,
        string elementName,
        string matchAttributeName,
        string matchAttributeValue,
        params string[] eventAttributeNames)
    {
        foreach (var frames in ComponentIds(componentId).Select(GetFrames))
        {
            for (var index = 0; index < frames.Count; index++)
            {
                var frame = frames[index];
                if (frame.FrameType != RenderTreeFrameType.Element ||
                    !string.Equals(frame.ElementName, elementName, StringComparison.Ordinal))
                {
                    continue;
                }

                var end = Math.Min(index + frame.ElementSubtreeLength, frames.Count);
                if (!ContainsAttribute(frames, index, end, matchAttributeName, matchAttributeValue))
                    continue;

                var handlerId = FindEventHandlerId(frames, index, end, eventAttributeNames);
                if (handlerId != 0)
                    return handlerId;
            }
        }

        throw new InvalidOperationException(
            $"Could not find the {elementName} element with {matchAttributeName} '{matchAttributeValue}'.");
    }

    internal Task DispatchEventAsync(ulong eventHandlerId, EventArgs eventArgs) =>
        Dispatcher.InvokeAsync(() => base.DispatchEventAsync(eventHandlerId, null, eventArgs));

    internal Task WaitForRenderCountAsync(int expected, CancellationToken cancellationToken)
    {
        lock (_renderGate)
        {
            if (RenderCount >= expected)
                return Task.CompletedTask;

            var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _renderWaiters.Add((expected, signal));
            return signal.Task.WaitAsync(cancellationToken);
        }
    }

    internal async Task WaitForTextContentAsync(int componentId, string expectedText, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(expectedText);
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await Dispatcher.InvokeAsync(() =>
        {
            lock (_renderGate)
            {
                if (GetTextContent(componentId).Contains(expectedText, StringComparison.Ordinal))
                    signal.TrySetResult();
                else
                    _textWaiters.Add((componentId, expectedText, signal));
            }
        });
        await signal.Task.WaitAsync(cancellationToken);
    }

    protected override Task UpdateDisplayAsync(in RenderBatch renderBatch)
    {
        lock (_renderGate)
        {
            var renderCount = Interlocked.Increment(ref _renderCount);
            foreach (var waiter in _renderWaiters.Where(waiter => waiter.Expected <= renderCount).ToArray())
            {
                waiter.Signal.TrySetResult();
                _renderWaiters.Remove(waiter);
            }
            foreach (var waiter in _textWaiters.Where(waiter =>
                         GetTextContent(waiter.ComponentId).Contains(waiter.ExpectedText, StringComparison.Ordinal)).ToArray())
            {
                waiter.Signal.TrySetResult();
                _textWaiters.Remove(waiter);
            }
        }

        return InvokeAfterRender ? CompleteAsynchronously() : Task.FromCanceled(new CancellationToken(true));
    }

    protected override void HandleException(Exception exception) =>
        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception).Throw();

    protected override IComponent ResolveComponentForRenderMode(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] Type componentType,
        int? parentComponentId,
        IComponentActivator componentActivator,
        IComponentRenderMode renderMode) =>
        componentActivator.CreateInstance(componentType);

    private IEnumerable<int> ComponentIds(int rootComponentId)
    {
        var pending = new Stack<int>();
        var visited = new HashSet<int>();
        pending.Push(rootComponentId);
        while (pending.TryPop(out var componentId))
        {
            if (!visited.Add(componentId))
                continue;

            yield return componentId;
            foreach (var frame in GetFrames(componentId))
            {
                if (frame.FrameType == RenderTreeFrameType.Component)
                    pending.Push(frame.ComponentId);
            }
        }
    }

    private static bool ContainsAttribute(
        IReadOnlyList<RenderTreeFrame> frames,
        int start,
        int end,
        string name,
        string value) =>
        frames.Skip(start).Take(end - start).Any(frame =>
            frame.FrameType == RenderTreeFrameType.Attribute &&
            string.Equals(frame.AttributeName, name, StringComparison.Ordinal) &&
            string.Equals(frame.AttributeValue?.ToString(), value, StringComparison.Ordinal));

    private static string? FindAttributeValue(
        IReadOnlyList<RenderTreeFrame> frames,
        int start,
        int end,
        string name) =>
        frames.Skip(start).Take(end - start)
            .FirstOrDefault(frame => frame.FrameType == RenderTreeFrameType.Attribute &&
                string.Equals(frame.AttributeName, name, StringComparison.Ordinal))
            .AttributeValue?.ToString();

    private static ulong FindEventHandlerId(
        IReadOnlyList<RenderTreeFrame> frames,
        int start,
        int end,
        IEnumerable<string> eventAttributeNames) =>
        frames.Skip(start).Take(end - start)
            .FirstOrDefault(frame => frame.FrameType == RenderTreeFrameType.Attribute &&
                eventAttributeNames.Contains(frame.AttributeName, StringComparer.Ordinal))
            .AttributeEventHandlerId;

    private static async Task CompleteAsynchronously() => await Task.Yield();
}
#pragma warning restore BL0006
