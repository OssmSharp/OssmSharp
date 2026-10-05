using System.Reflection;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.RenderTree;

namespace OssmSharp.Web.Api.Tests;

#pragma warning disable BL0006 // Deliberate test-only access to the existing Razor render branches.

// These tests intentionally exercise state/calculations, not browser markup or lifecycle wiring.
internal static class ComponentTestAccess
{
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    internal static T Get<T>(object component, string name)
    {
        var type = component.GetType();
        var value = type.GetProperty(name, Members) is { } property
            ? property.GetValue(component)
            : (type.GetField(name, Members) ?? throw new MissingFieldException(type.FullName, name)).GetValue(component);
        return (T)value!;
    }

    internal static void Set(object component, string name, object? value)
    {
        var type = component.GetType();
        if (type.GetProperty(name, Members) is { } property)
            property.SetValue(component, value);
        else
            (type.GetField(name, Members) ?? throw new MissingFieldException(type.FullName, name)).SetValue(component, value);
    }

    internal static object? Invoke(object component, string name, params object?[] arguments)
        => (component.GetType().GetMethod(name, Members) ?? throw new MissingMethodException(component.GetType().FullName, name))
            .Invoke(component, arguments);

    internal static Task InvokeTask(object component, string name, params object?[] arguments)
        => (Task)Invoke(component, name, arguments)!;

    internal static void Inject(object component, string name, object value) => Set(component, name, value);

    internal static Dictionary<string, bool> Buttons(object component)
    {
        var buttons = new Dictionary<string, bool>();
        foreach (var parameters in Parameters(component, "RwButton"))
        {
            var fragment = (RenderFragment)parameters["ChildContent"]!;
            using var builder = new RenderTreeBuilder();
            fragment(builder);
            var frames = builder.GetFrames();
            var label = string.Concat(frames.Array.Take(frames.Count)
                .Where(frame => frame.FrameType is RenderTreeFrameType.Text or RenderTreeFrameType.Markup)
                .Select(frame => frame.FrameType == RenderTreeFrameType.Text ? frame.TextContent : frame.MarkupContent)).Trim();
            buttons.Add(label, parameters.TryGetValue("Disabled", out var disabled) && disabled is true);
        }
        return buttons;
    }

    internal static IEnumerable<Dictionary<string, object?>> Parameters(object component, string typeName)
    {
        foreach (var frames in ContentFrames(component))
        {
            for (var index = 0; index < frames.Length; index++)
            {
                if (frames[index].FrameType != RenderTreeFrameType.Component || frames[index].ComponentType.Name != typeName)
                    continue;
                var end = index + frames[index].ComponentSubtreeLength;
                yield return frames.Skip(index + 1).Take(end - index - 1)
                    .Where(frame => frame.FrameType == RenderTreeFrameType.Attribute)
                    .ToDictionary(frame => frame.AttributeName, frame => (object?)frame.AttributeValue);
            }
        }
    }

    internal static string ElementText(object component, string testId)
    {
        foreach (var frames in ContentFrames(component))
        {
            for (var index = 0; index < frames.Length; index++)
            {
                if (frames[index].FrameType != RenderTreeFrameType.Element)
                    continue;
                var content = frames.Skip(index + 1).Take(frames[index].ElementSubtreeLength - 1).ToArray();
                if (content.TakeWhile(frame => frame.FrameType == RenderTreeFrameType.Attribute)
                    .Any(frame => frame.AttributeName == "data-testid" && Equals(frame.AttributeValue, testId)))
                    return string.Concat(content.Where(frame => frame.FrameType is RenderTreeFrameType.Text or RenderTreeFrameType.Markup)
                        .Select(frame => frame.FrameType == RenderTreeFrameType.Text ? frame.TextContent : frame.MarkupContent)).Trim();
            }
        }
        throw new InvalidOperationException($"No rendered element for {testId}.");
    }

    private static IEnumerable<RenderTreeFrame[]> ContentFrames(object component)
    {
        using var builder = new RenderTreeBuilder();
        Invoke(component, "BuildRenderTree", builder);
        var pending = new Queue<RenderTreeFrame[]>();
        var initial = builder.GetFrames();
        pending.Enqueue(initial.Array.Take(initial.Count).ToArray());
        while (pending.TryDequeue(out var frames))
        {
            yield return frames;
            foreach (var fragment in frames.Where(frame => frame.FrameType == RenderTreeFrameType.Attribute)
                .Select(frame => frame.AttributeValue).OfType<RenderFragment>())
            {
                builder.Clear();
                fragment(builder);
                var children = builder.GetFrames();
                pending.Enqueue(children.Array.Take(children.Count).ToArray());
            }
        }
    }
}
