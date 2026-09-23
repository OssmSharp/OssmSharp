using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace OSSMDotNet.ControlClient.Tests;

/// <summary>
/// Checks service-key transport setup and validation of control hub connection options.
/// </summary>
public sealed class ControlHubConnectionFactoryTests
{
    private const string ValidKey = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaA";

    [Fact]
    public async Task CreateConnectsOnlyToTheVersionedHubUsingTheConfiguredServiceKeyAndWebSockets()
    {
        using var cancellationSource = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellationSource.CancelAfter(TimeSpan.FromSeconds(5));
        await using var fixture = await LocalControlHubFixture.StartAsync(cancellationSource.Token);
        var options = fixture.CreateOptions();
        await using HubConnection connection = new ControlHubConnectionFactory(options).Create();

        await connection.StartAsync(cancellationSource.Token);
        await fixture.WaitForConnectionAsync(cancellationSource.Token);

        Assert.Equal("/hubs/control/v1", fixture.ConnectionPath);
        Assert.Equal($"ServiceKey {ValidKey}", fixture.AuthorizationHeader);
        Assert.True(fixture.UsedWebSockets);
        Assert.Equal(TimeSpan.FromSeconds(3), connection.ServerTimeout);
        Assert.Equal(TimeSpan.FromSeconds(1), connection.KeepAliveInterval);
    }

    [Theory]
    [InlineData("http://127.0.0.1:1/", 1, 2, 1)]
    [InlineData("https://control.example:65535/", 300, 600, 30)]
    public void ExactOptionBoundariesWithTrustedOriginsAreAccepted(
        string origin, int keepAliveSeconds, int serverTimeoutSeconds, int commandTimeoutSeconds)
    {
        var options = new ControlClientOptions
        {
            ControlOrigin = origin,
            ServiceKey = ValidKey,
            KeepAliveSeconds = keepAliveSeconds,
            ServerTimeoutSeconds = serverTimeoutSeconds,
            CommandTimeoutSeconds = commandTimeoutSeconds
        };

        var results = Validate(options);

        Assert.Empty(results);
        Assert.Equal(origin, options.ControlOrigin);
        Assert.Equal(keepAliveSeconds, options.KeepAliveSeconds);
        Assert.Equal(serverTimeoutSeconds, options.ServerTimeoutSeconds);
        Assert.Equal(commandTimeoutSeconds, options.CommandTimeoutSeconds);
    }

    [Theory]
    [InlineData("ftp://127.0.0.1:5181/")]
    [InlineData("http://control.example:5181/")]
    [InlineData("https://127.0.0.1:5181/")]
    [InlineData("http://127.0.0.1:5181/hubs/control/v1")]
    [InlineData("http://127.0.0.1:5181/?query=value")]
    [InlineData("http://127.0.0.1:5181/#fragment")]
    [InlineData("http://key@127.0.0.1:5181/")]
    [InlineData("http://127.0.0.1:0/")]
    [InlineData("http://127.0.0.1:65536/")]
    [InlineData("not an origin")]
    public void MissingOrUnsafeOriginsAreRejectedWithoutNormalization(string origin)
    {
        var options = new ControlClientOptions { ControlOrigin = origin, ServiceKey = ValidKey };

        var results = Validate(options);

        Assert.Contains(results, result =>
            result.MemberNames.Contains(nameof(ControlClientOptions.ControlOrigin)) &&
            result.ErrorMessage == "ControlOrigin must be a path-free loopback HTTP origin or a non-loopback HTTPS origin.");
        Assert.Equal(origin, options.ControlOrigin);
    }

    [Fact]
    public void EmptyOriginIsRejectedByTheRequiredBoundaryWithoutAReplacementValue()
    {
        var options = new ControlClientOptions { ControlOrigin = "", ServiceKey = ValidKey };

        var results = Validate(options);

        var result = Assert.Single(results);
        Assert.Equal("The ControlOrigin field is required.", result.ErrorMessage);
        Assert.Equal("", options.ControlOrigin);
    }

    [Fact]
    public void NullOriginIsRejectedWithoutReplacingTheValue()
    {
        var options = new ControlClientOptions { ControlOrigin = null!, ServiceKey = ValidKey };

        var results = Validate(options);

        Assert.Contains(results, result => result.MemberNames.Contains(nameof(ControlClientOptions.ControlOrigin)));
        Assert.Null(options.ControlOrigin);
    }

    [Theory]
    [InlineData("")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaB")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaA=")]
    public void MissingOrMalformedServiceKeysAreRejected(string key)
    {
        var options = new ControlClientOptions { ControlOrigin = "http://127.0.0.1:5181/", ServiceKey = key };

        var results = Validate(options);

        Assert.Contains(results, result => result.MemberNames.Contains(nameof(ControlClientOptions.ServiceKey)));
        Assert.Equal(key, options.ServiceKey);
    }

    [Fact]
    public void NullServiceKeyIsRejectedWithoutReplacingTheValue()
    {
        var options = new ControlClientOptions { ControlOrigin = "http://127.0.0.1:5181/", ServiceKey = null! };

        var results = Validate(options);

        Assert.Contains(results, result => result.MemberNames.Contains(nameof(ControlClientOptions.ServiceKey)));
        Assert.Null(options.ServiceKey);
    }

    [Theory]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaA")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaQ")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaag")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaw")]
    public void EachAllowedServiceKeyTerminalClassIsAcceptedWithoutTransformation(string key)
    {
        var options = new ControlClientOptions
        {
            ControlOrigin = "http://[::1]:5181/",
            ServiceKey = key,
            KeepAliveSeconds = 1,
            ServerTimeoutSeconds = 2,
            CommandTimeoutSeconds = 1
        };

        var results = Validate(options);

        Assert.Empty(results);
        Assert.Equal(key, options.ServiceKey);
    }

    [Theory]
    [InlineData(0, 3, 2, nameof(ControlClientOptions.KeepAliveSeconds))]
    [InlineData(301, 602, 2, nameof(ControlClientOptions.KeepAliveSeconds))]
    [InlineData(1, 1, 2, nameof(ControlClientOptions.ServerTimeoutSeconds))]
    [InlineData(300, 601, 2, nameof(ControlClientOptions.ServerTimeoutSeconds))]
    [InlineData(2, 3, 2, nameof(ControlClientOptions.ServerTimeoutSeconds))]
    [InlineData(1, 2, 0, nameof(ControlClientOptions.CommandTimeoutSeconds))]
    [InlineData(1, 2, 31, nameof(ControlClientOptions.CommandTimeoutSeconds))]
    public void InvalidTimeoutPartitionsAreRejected(
        int keepAliveSeconds, int serverTimeoutSeconds, int commandTimeoutSeconds, string member)
    {
        var options = new ControlClientOptions
        {
            ControlOrigin = "http://127.0.0.1:5181/",
            ServiceKey = ValidKey,
            KeepAliveSeconds = keepAliveSeconds,
            ServerTimeoutSeconds = serverTimeoutSeconds,
            CommandTimeoutSeconds = commandTimeoutSeconds
        };

        var results = Validate(options);

        Assert.Contains(results, result => result.MemberNames.Contains(member));
        Assert.Equal(keepAliveSeconds, options.KeepAliveSeconds);
        Assert.Equal(serverTimeoutSeconds, options.ServerTimeoutSeconds);
        Assert.Equal(commandTimeoutSeconds, options.CommandTimeoutSeconds);
    }

    [Fact]
    public async Task RegistrationResolvesScopedPublicClientsWithoutOpeningAConnection()
    {
        using var cancellationSource = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellationSource.CancelAfter(TimeSpan.FromSeconds(5));
        await using var fixture = await LocalControlHubFixture.StartAsync(cancellationSource.Token);
        using var services = CreateServices(new Dictionary<string, string?>
        {
            ["ControlClient:ControlOrigin"] = fixture.ControlOrigin,
            ["ControlClient:ServiceKey"] = ValidKey,
            ["ControlClient:KeepAliveSeconds"] = "1",
            ["ControlClient:ServerTimeoutSeconds"] = "3",
            ["ControlClient:CommandTimeoutSeconds"] = "2"
        });

        await using var scope = services.CreateAsyncScope();
        var configured = scope.ServiceProvider.GetRequiredService<IOptions<ControlClientOptions>>().Value;
        var factory = scope.ServiceProvider.GetRequiredService<ControlHubConnectionFactory>();
        var operatorClient = scope.ServiceProvider.GetRequiredService<OperatorControlClient>();
        var guestClient = scope.ServiceProvider.GetRequiredService<GuestControlClient>();

        Assert.Equal(fixture.ControlOrigin, configured.ControlOrigin);
        Assert.NotNull(factory);
        Assert.NotNull(operatorClient);
        Assert.NotNull(guestClient);
        Assert.Equal(0, fixture.ConnectionCount);
    }

    [Fact]
    public async Task RegistrationRejectsInvalidConfigurationBeforeAnyConnectionIsCreated()
    {
        using var cancellationSource = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellationSource.CancelAfter(TimeSpan.FromSeconds(5));
        await using var fixture = await LocalControlHubFixture.StartAsync(cancellationSource.Token);
        using var services = CreateServices(new Dictionary<string, string?>
        {
            ["ControlClient:ControlOrigin"] = $"{fixture.ControlOrigin}unsafe",
            ["ControlClient:ServiceKey"] = ValidKey
        });

        var exception = Assert.Throws<OptionsValidationException>(() =>
            _ = services.GetRequiredService<IOptions<ControlClientOptions>>().Value);

        Assert.Contains("ControlOrigin", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, fixture.ConnectionCount);
    }

    [Fact]
    public void RegistrationRejectsUnknownConfigurationInsteadOfSilentlyIgnoringIt()
    {
        using var services = CreateServices(new Dictionary<string, string?>
        {
            ["ControlClient:ControlOrigin"] = "http://127.0.0.1:5181/",
            ["ControlClient:ServiceKey"] = ValidKey,
            ["ControlClient:Unexpected"] = "ignored"
        });

        var exception = Assert.Throws<InvalidOperationException>(() =>
            _ = services.GetRequiredService<IOptions<ControlClientOptions>>().Value);

        Assert.Contains("Unexpected", exception.Message, StringComparison.Ordinal);
    }

    private static IReadOnlyList<ValidationResult> Validate(ControlClientOptions options)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(options, new(options), results, validateAllProperties: true);
        return results;
    }

    private static ServiceProvider CreateServices(IEnumerable<KeyValuePair<string, string?>> values)
    {
        var collection = new ServiceCollection();
        collection.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
        collection.AddControlClient("ControlClient");
        return collection.BuildServiceProvider();
    }
}
