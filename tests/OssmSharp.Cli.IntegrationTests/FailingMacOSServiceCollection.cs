#if MACOS
using System.Collections.ObjectModel;
using Microsoft.Extensions.DependencyInjection;
using Shiny.BluetoothLE;

namespace OssmSharp.Cli.IntegrationTests;

internal sealed class FailingMacOSServiceCollection : Collection<ServiceDescriptor>, IServiceCollection
{
    internal InvalidOperationException Failure { get; } = new("Registration failed after acquiring the dispatch queue.");

    internal AppleBleConfiguration? Configuration { get; private set; }

    protected override void InsertItem(int index, ServiceDescriptor item)
    {
        if (Configuration is not null)
            throw Failure;

        if (!item.IsKeyedService && item.ImplementationInstance is AppleBleConfiguration configuration)
            Configuration = configuration;

        base.InsertItem(index, item);
    }
}
#endif
