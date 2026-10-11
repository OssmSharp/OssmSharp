using Microsoft.JSInterop;

namespace OssmSharp.Web.Api.Tests;

internal sealed class RecordingJsModule : IJSObjectReference
{
    internal List<(string Method, object?[] Arguments)> Calls { get; } = [];

    internal string? Invitation { get; set; } = "synthetic-invitation";
    internal Func<CancellationToken, Task>? BeforeInvoke { get; set; }

    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        => InvokeAsync<TValue>(identifier, CancellationToken.None, args);

    public async ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
    {
        Calls.Add((identifier, args ?? []));
        if (BeforeInvoke is not null)
            await BeforeInvoke(cancellationToken);
        return identifier == "takeInvitation" ? (TValue)(object)Invitation! : default!;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
