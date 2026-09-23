namespace OSSMDotNet.GuestWeb.Tests;

/// <summary>
/// Supplies the recorded guest module for JavaScript imports and rejects other runtime calls.
/// </summary>
/// <param name="module">The module returned for import calls.</param>
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
