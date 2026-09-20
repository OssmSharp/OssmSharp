namespace OSSMDotNet.GuestWeb.Tests;

internal sealed class RecordedJsRuntime(RecordedJsModule module) : IJSRuntime
{
    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
        InvokeAsync<TValue>(identifier, CancellationToken.None, args);

    public ValueTask<TValue> InvokeAsync<TValue>(
        string identifier,
        CancellationToken cancellationToken,
        object?[]? args)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(identifier, "import", StringComparison.Ordinal))
            throw new JSException($"Unexpected JavaScript runtime call: {identifier}");

        return new((TValue)(object)module);
    }
}
