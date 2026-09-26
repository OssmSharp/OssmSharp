using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace OssmSharp.Web.Client.Tests;

/// <summary>
/// Checks client service registration and validation of configuration on resolution.
/// </summary>
public sealed class ControlClientServiceCollectionTests
{
    private const string ValidKey = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaA";

    [Fact]
    public void EmptyConfigurationSectionIsRejectedBeforeServicesAreRegistered()
    {
        var services = new ServiceCollection();

        var exception = Assert.Throws<ArgumentException>(() => services.AddControlClient(""));

        Assert.Contains("configurationSection", exception.Message, StringComparison.Ordinal);
        Assert.Empty(services);
    }

    [Fact]
    public async Task RegisteredFactoryAndPublicClientsUseTheValidatedConfiguredOptions()
    {
        using var cancellationSource = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellationSource.CancelAfter(TimeSpan.FromSeconds(5));
        await using var fixture = await LocalControlHubFixture.StartAsync(cancellationSource.Token);
        await using var services = CreateServices(new Dictionary<string, string?>
        {
            ["Control:ControlOrigin"] = fixture.ControlOrigin,
            ["Control:ServiceKey"] = ValidKey,
            ["Control:KeepAliveSeconds"] = "1",
            ["Control:ServerTimeoutSeconds"] = "3",
            ["Control:CommandTimeoutSeconds"] = "2"
        });
        await using var scope = services.CreateAsyncScope();

        var options = scope.ServiceProvider.GetRequiredService<IOptions<ControlClientOptions>>().Value;
        var factory = scope.ServiceProvider.GetRequiredService<ControlHubConnectionFactory>();
        var operatorClient = scope.ServiceProvider.GetRequiredService<OperatorControlClient>();
        var guestClient = scope.ServiceProvider.GetRequiredService<GuestControlClient>();

        Assert.Equal(fixture.ControlOrigin, options.ControlOrigin);
        Assert.Equal(2, options.CommandTimeoutSeconds);
        Assert.Same(factory, scope.ServiceProvider.GetRequiredService<ControlHubConnectionFactory>());
        Assert.NotSame(operatorClient, guestClient);
        Assert.Equal(0, fixture.ConnectionCount);
    }

    [Fact]
    public async Task RegisteredOptionsRejectInvalidCrossFieldTimeoutBeforeAClientCanBeResolved()
    {
        await using var services = CreateServices(new Dictionary<string, string?>
        {
            ["Control:ControlOrigin"] = "http://127.0.0.1:5181/",
            ["Control:ServiceKey"] = ValidKey,
            ["Control:KeepAliveSeconds"] = "2",
            ["Control:ServerTimeoutSeconds"] = "3"
        });

        var exception = Assert.Throws<OptionsValidationException>(() =>
            _ = services.GetRequiredService<IOptions<ControlClientOptions>>().Value);

        Assert.Contains("ServerTimeoutSeconds must be at least twice KeepAliveSeconds.", exception.Message,
            StringComparison.Ordinal);
    }

    private static ServiceProvider CreateServices(IEnumerable<KeyValuePair<string, string?>> values)
    {
        var collection = new ServiceCollection();
        collection.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
        collection.AddControlClient("Control");
        return collection.BuildServiceProvider();
    }
}
