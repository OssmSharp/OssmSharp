namespace OssmSharp.Web.GuestWeb.Components.Pages;

public partial class Join
{
    /// <summary>
    /// Accepts the latest browser-side display state for the local preview.
    /// </summary>
    /// <param name="display">The browser's current selection and bounded feedback text.</param>
    /// <exception cref="ArgumentException">The display has missing values or feedback longer than 200 characters.</exception>
    [JSInvokable]
    public Task UpdateDisplay(GuestDisplayState display)
    {
        if (_disposed)
            return Task.CompletedTask;
        if (display.Selected is null || display.Status is not { Length: <= 200 } || display.Error?.Length > 200)
            throw new ArgumentException("Invalid guest display state.", nameof(display));
        _display = display;
        StateHasChanged();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Validates and submits complete guest settings from the untrusted browser.
    /// </summary>
    /// <param name="settings">The complete requested settings, including the unchanged speed.</param>
    /// <returns>The accepted reply code, or a rejection or unknown-outcome code.</returns>
    [JSInvokable]
    public async Task<BrowserReply> SubmitSettings(BrowserSettings settings)
    {
        if (!Ready)
            return new(false, "not-ready");
        if (settings is not { Pattern: >= 0 and <= 6, Sensation: >= 0 and <= 100, Speed: >= 0 and <= 100, Stroke: >= 0 and <= 100, Depth: >= 0 and <= 100 }
            || settings.Stroke > settings.Depth)
        {
            return new(false, "invalid-input");
        }

        try
        {
            var response = await Client.SubmitAsync(
                new(Guid.NewGuid().ToString("N"), settings.Pattern, settings.Speed, settings.Stroke, settings.Depth, settings.Sensation),
                _lifetime.Token);
            return new(response.Status == SettingsResponseStatus.Completed, response.ErrorCode ?? response.Status.ToString());
        }
        catch (RpcClientRequestTimeoutException)
        {
            return new(false, "outcome-unknown");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return new(false, "outcome-unknown");
        }
        catch (ApiClientException error)
        {
            return new(false, error.Code);
        }
    }

    /// <summary>
    /// Stops guest motion without treating command acceptance as device verification.
    /// </summary>
    /// <returns>The accepted reply code, or a rejection or unknown-outcome code.</returns>
    [JSInvokable]
    public Task<BrowserReply> StopRun()
        => RunPositionCommandAsync(resetPosition: false);

    /// <summary>
    /// Retracts once and stops for the permitted guest without resuming a pattern.
    /// </summary>
    /// <returns>The completed reply code or the service's failure code.</returns>
    [JSInvokable]
    public Task<BrowserReply> ResetPosition()
        => RunPositionCommandAsync(resetPosition: true);

    /// <summary>
    /// Cancels and awaits polling before releasing browser and control resources.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        await _lifetime.CancelAsync();
        if (_pollingTask is not null)
            await _pollingTask;
        await Client.DisposeAsync();
        if (_module is not null)
        {
            try
            {
                await _module.InvokeVoidAsync("dispose");
                await _module.DisposeAsync();
            }
            catch (JSDisconnectedException) { }
        }
        _reference?.Dispose();
        _lifetime.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task<BrowserReply> RunPositionCommandAsync(bool resetPosition)
    {
        if (_disposed || !_joined)
            return new(false, "not-joined");
        if (resetPosition && !Ready)
            return new(false, "not-ready");
        try
        {
            var response = await (resetPosition ? Client.ResetPositionAsync(_lifetime.Token) : Client.StopAsync(_lifetime.Token));
            return new(response.Accepted, response.Code);
        }
        catch (RpcClientRequestTimeoutException)
        {
            return new(false, "outcome-unknown");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return new(false, "outcome-unknown");
        }
        catch (ApiClientException error)
        {
            return new(false, error.Code);
        }
    }
}
