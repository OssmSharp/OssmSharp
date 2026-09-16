using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OSSMDotNet.Models;
using OSSMDotNet.AdminService;
using OSSMDotNet.ControlCore;
using OSSMDotNet.ControlCore.Tests;
using OSSMDotNet.GuestWeb.Services;
using OSSMDotNet.ServiceDefaults;

namespace OSSMDotNet.AdminService.IntegrationTests;

public sealed class TelemetryTests
{
    [Fact]
    public async Task GuestReceivesActualSettingsAndMotionThroughTheRestrictedPipe()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        var token = CapabilityTokens.NewToken();
        var pipeName = $"telemetry-{Guid.NewGuid():N}";
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OSSM:AdminPort"] = "5181",
            ["OSSM:GuestOrigin"] = "http://127.0.0.1:5182",
            ["OSSM:PipeName"] = pipeName,
            ["OSSM:GuestServiceToken"] = token,
            ["OSSM:SimulatorPath"] = "unused"
        });
        var bridge = new FakeBridge { Position = 73.5, Velocity = -6.25 };
        builder.Services.AddAdminConfiguration();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<IDeviceBridge>(bridge);
        builder.Services.AddSingleton<PrototypeEngine>();
        builder.Services.AddHostedService<GuestPipeServer>();
        using var host = builder.Build();
        var engine = host.Services.GetRequiredService<PrototypeEngine>();
        bridge.Publish();
        await host.StartAsync(deadline.Token);
        await using var client = new GuestSessionClient(Options.Create(new GuestOptions
        {
            GuestPort = 5182,
            PipeName = pipeName,
            GuestServiceToken = token
        }), NullLogger<GuestSessionClient>.Instance);
        try
        {
            var invitation = engine.CreateInvitation(Guid.NewGuid(), new Uri("http://127.0.0.1:5182"), "operator");
            var secret = new Uri(invitation.Url).Fragment["#invite=".Length..];
            var reply = await client.JoinAsync(invitation.SessionId, secret, deadline.Token);
            Assert.True(reply.Ok, reply.Code);
            Assert.NotNull(reply.State);
            Assert.Equal(73.5, reply.State.Position);
            Assert.Equal(-6.25, reply.State.Velocity);
            Assert.NotNull(reply.State.Current);
            Assert.Equal(0, reply.State.Current.Speed);
            Assert.Equal(10, reply.State.Current.Stroke);
            Assert.Equal(20, reply.State.Current.Depth);
            Assert.Equal(0, reply.State.VerifiedSeq);
            Assert.False(reply.State.DeviceEnabled);
            Assert.True(reply.State.IsSimulator);
            Assert.Null(reply.State.ConnectionId);
            bridge.External(DesiredState.Stopped with { Speed = 10 });
            var running = await client.StatusAsync(deadline.Token);
            Assert.True(running.Ok, running.Code);
            Assert.True(running.State!.DeviceEnabled);
        }
        finally
        {
            await client.DisposeAsync();
            await host.StopAsync(deadline.Token);
        }
    }
}
