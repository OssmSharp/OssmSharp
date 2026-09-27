#if MACOS
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OssmSharp.Cli.Interop;
using Shiny.BluetoothLE;

namespace OssmSharp.Cli.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class MacOSPlatformServicesTests
{
    [Fact]
    public void RegistrationFailureReleasesAcquiredDispatchQueue()
    {
        var services = new FailingMacOSServiceCollection();
        try
        {
            var failure = Assert.Throws<InvalidOperationException>(() =>
            {
                using var lifetime = services.AddCliPlatformServices();
            });

            Assert.Same(services.Failure, failure);
            var configuration = Assert.IsType<AppleBleConfiguration>(services.Configuration);
            Assert.Equal(IntPtr.Zero, (IntPtr)configuration.DispatchQueue!.Handle);
        }
        finally
        {
            services.Configuration?.DispatchQueue?.Dispose();
        }
    }

    [Fact]
    public void HostDisposalLeavesDispatchQueueOwnedByCaller()
    {
        var builder = new HostApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        AppleBleConfiguration configuration;
        MacOSQueueDisposalObserver observer;
        using (var lifetime = builder.Services.AddCliPlatformServices())
        {
            builder.Services.AddSingleton(provider =>
                new MacOSQueueDisposalObserver(provider.GetRequiredService<AppleBleConfiguration>()));
            using (var host = builder.Build())
            {
                configuration = host.Services.GetRequiredService<AppleBleConfiguration>();
                Assert.Same(lifetime, configuration.DispatchQueue);
                observer = host.Services.GetRequiredService<MacOSQueueDisposalObserver>();
                Assert.NotEqual(IntPtr.Zero, (IntPtr)configuration.DispatchQueue!.Handle);
            }

            Assert.Equal(1, observer.DisposeCount);
            Assert.True(observer.QueueWasAliveOnDispose);
            Assert.NotEqual(IntPtr.Zero, (IntPtr)configuration.DispatchQueue!.Handle);
        }

        Assert.Equal(IntPtr.Zero, (IntPtr)configuration.DispatchQueue!.Handle);
        Assert.Equal(1, observer.DisposeCount);
    }

    [Fact]
    public void HostBuildFailureReleasesDispatchQueueWhenCallerScopeEnds()
    {
        var builder = new HostApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        AppleBleConfiguration configuration;
        using (var lifetime = builder.Services.AddCliPlatformServices())
        {
            configuration = Assert.IsType<AppleBleConfiguration>(
                Assert.Single(builder.Services, descriptor =>
                    descriptor.ServiceType == typeof(AppleBleConfiguration)).ImplementationInstance);
            Assert.Same(lifetime, configuration.DispatchQueue);
            builder.Services.AddSingleton(typeof(IDisposable), typeof(IDisposable));

            Assert.Throws<ArgumentException>(() =>
            {
                using var host = builder.Build();
            });
            Assert.NotEqual(IntPtr.Zero, (IntPtr)configuration.DispatchQueue!.Handle);
        }

        Assert.Equal(IntPtr.Zero, (IntPtr)configuration.DispatchQueue!.Handle);
    }
}
#endif
