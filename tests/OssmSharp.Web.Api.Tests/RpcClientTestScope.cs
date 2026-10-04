using Microsoft.Extensions.Options;
using OssmSharp.Web.Client.Connections;
using OssmSharp.Web.Client.DependencyInjection;
using OssmSharp.Web.Client.Models;

namespace OssmSharp.Web.Api.Tests;

internal sealed class RpcClientTestScope : IAsyncDisposable
{
    private readonly ServiceProvider _provider;
    private readonly AsyncServiceScope _scope;

    public RpcClientTestScope(RpcClientOptions options, bool guest = false)
    {
        var services = new ServiceCollection();
        if (guest)
            services.AddGuestApiClient("Client");
        else
            services.AddAdminApiClient("Client");
        services.AddSingleton(Options.Create(options));
        _provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        _scope = _provider.CreateAsyncScope();
    }

    public IRpcClient Connection => _scope.ServiceProvider.GetRequiredService<IRpcClient>();

    public async ValueTask DisposeAsync()
    {
        try { await _scope.DisposeAsync(); }
        finally { await _provider.DisposeAsync(); }
    }
}
