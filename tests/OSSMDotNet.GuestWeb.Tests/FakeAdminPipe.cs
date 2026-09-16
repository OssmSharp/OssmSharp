using System.IO.Pipes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using OSSMDotNet.GuestWeb.Services;
using OSSMDotNet.ServiceDefaults;

namespace OSSMDotNet.GuestWeb.Tests;

internal sealed class FakeAdminPipe : IAsyncDisposable
{
    private readonly CancellationTokenSource _lifetime = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
    private readonly NamedPipeServerStream _server;
    private readonly Task _completion;
    public string PipeName { get; } = $"guest-tests-{Guid.NewGuid():N}";
    public FakeLogger<GuestSessionClient> Logger { get; } = new();

    public FakeAdminPipe(Func<NamedPipeServerStream, CancellationToken, Task> handler)
    {
        _lifetime.CancelAfter(TimeSpan.FromSeconds(10));
        _server = new(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        _completion = RunAsync(handler);
    }

    public GuestSessionClient CreateClient() => CreateClient(PipeName, Logger);

    public static GuestSessionClient CreateClient(string pipeName, ILogger<GuestSessionClient>? logger = null) =>
        new(Options.Create(new GuestOptions
        {
            GuestPort = 5123,
            PipeName = pipeName,
            GuestServiceToken = GuestSessionClientTests.ServiceToken
        }), logger ?? NullLogger<GuestSessionClient>.Instance);

    public Task CompleteAsync() => _completion;

    private async Task RunAsync(Func<NamedPipeServerStream, CancellationToken, Task> handler)
    {
        await using (_server)
        {
            await _server.WaitForConnectionAsync(_lifetime.Token);
            await handler(_server, _lifetime.Token);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync();
        _server.Dispose();
        try { await _completion; }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        finally { _lifetime.Dispose(); }
    }
}
