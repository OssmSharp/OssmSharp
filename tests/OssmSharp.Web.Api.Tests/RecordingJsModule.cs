using Microsoft.JSInterop;

namespace OssmSharp.Web.Api.Tests;

internal sealed class RecordingJsModule : IJSObjectReference
{
    internal List<(string Method, object?[] Arguments)> Calls { get; } = [];

    internal string? Invitation { get; set; } = "synthetic-invitation";

    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        => InvokeAsync<TValue>(identifier, CancellationToken.None, args);

    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
    {
        Calls.Add((identifier, args ?? []));
        return ValueTask.FromResult(identifier == "takeInvitation" ? (TValue)(object)Invitation! : default!);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
