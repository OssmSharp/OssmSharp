using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OSSMDotNet.ControlService;
using OSSMDotNet.ControlService.Models;

namespace OSSMDotNet.ControlService.Tests;

public sealed class ControlHubRegistrationTests
{
    [Fact]
    public void SignalRConfigurationBoundsInboundWorkAndDisablesSensitiveDiagnostics()
    {
        var collection = new ServiceCollection();
        collection.Configure<ControlServiceOptions>(options =>
        {
            options.KeepAliveSeconds = 2;
            options.ClientTimeoutSeconds = 6;
        });
        using var services = collection.AddControlServiceHub().BuildServiceProvider();

        var options = services.GetRequiredService<IOptions<HubOptions>>().Value;

        Assert.False(options.EnableDetailedErrors);
        Assert.True(options.DisableImplicitFromServicesParameters);
        Assert.Equal(16_384, options.MaximumReceiveMessageSize);
        Assert.Equal(1, options.MaximumParallelInvocationsPerClient);
        Assert.Equal(TimeSpan.FromSeconds(5), options.HandshakeTimeout);
        Assert.Equal(TimeSpan.FromSeconds(6), options.ClientTimeoutInterval);
        Assert.Equal(TimeSpan.FromSeconds(2), options.KeepAliveInterval);
        Assert.Equal(0, options.StatefulReconnectBufferSize);
    }

    [Fact]
    public void HubMappingUsesTheVersionedRouteAndRequiresAServicIdentity()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddControlServiceAuthentication();
        builder.Services.AddControlServiceAuthorization();
        builder.Services.AddControlServiceHub();
        using var app = builder.Build();

        app.MapControlHubV1();

        var endpoint = Assert.Single(((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints),
            candidate => candidate is RouteEndpoint route &&
                route.RoutePattern.RawText == "/hubs/control/v1");
        var authorization = Assert.Single(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>());

        Assert.Equal(ControlServicePolicies.Service, authorization.Policy);
        Assert.Equal(ServiceKeyAuthenticationDefaults.Scheme, authorization.AuthenticationSchemes);
    }
}
