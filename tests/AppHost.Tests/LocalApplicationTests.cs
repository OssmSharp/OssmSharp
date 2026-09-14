using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OSSMDotNet.Web.ControlCore;
using OSSMDotNet.Web.GuestWeb.Services;
using OSSMDotNet.Web.HostRuntime;

namespace OSSMDotNet.Web.AppHost.Tests;

public sealed class LocalApplicationTests
{
    [Fact]
    public async Task AspireStartsIsolatedServicesAndTheRealSimulatorSupportsTheOperatorJourney()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        var cancellation = deadline.Token;
        await using var builder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.AppHost>(cancellation);
        await using var app = await builder.BuildAsync(cancellation);
        await app.StartAsync(cancellation);
        await app.ResourceNotifications.WaitForResourceHealthyAsync("admin", cancellation);
        await app.ResourceNotifications.WaitForResourceHealthyAsync("guest", cancellation);

        using var admin = app.CreateHttpClient("admin", "http");
        admin.BaseAddress = new UriBuilder(admin.BaseAddress!) { Host = "127.0.0.1" }.Uri;
        admin.DefaultRequestHeaders.Add("Origin", admin.BaseAddress.GetLeftPart(UriPartial.Authority));
        using var guest = app.CreateHttpClient("guest", "http");
        guest.BaseAddress = new UriBuilder(guest.BaseAddress!) { Host = "127.0.0.1" }.Uri;
        using var guestPage = await guest.GetAsync("/", cancellation);
        Assert.Equal(HttpStatusCode.OK, guestPage.StatusCode);
        Assert.Contains("_framework/blazor.web.js", await guestPage.Content.ReadAsStringAsync(cancellation));
        using var forbidden = await guest.GetAsync("/api/v1/admin/status", cancellation);
        Assert.Equal(HttpStatusCode.NotFound, forbidden.StatusCode);

        var operatorParameter = builder.Resources.OfType<ParameterResource>().Single(resource => resource.Name == "operator-token");
        var operatorToken = await operatorParameter.GetValueAsync(cancellation);
        admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", operatorToken);
        var adminResource = builder.Resources.OfType<ProjectResource>().Single(resource => resource.Name == "admin");
        var execution = await ExecutionConfigurationBuilder.Create(adminResource).WithEnvironmentVariablesConfig()
            .BuildAsync(builder.ExecutionContext, NullLogger.Instance, cancellation);
        var environment = execution.EnvironmentVariables.ToDictionary();
        await using var client = new GuestSessionClient(Options.Create(new GuestOptions
        {
            GuestPort = guest.BaseAddress.Port,
            PipeName = environment["OSSM__PipeName"],
            GuestServiceToken = environment["OSSM__GuestServiceToken"]
        }), NullLogger<GuestSessionClient>.Instance);

        await WaitUntil(admin, state => state.BridgeReady && !state.DeviceMoving, cancellation);
        await Post(admin, "recover", null, cancellation);
        await Post(admin, "limits", new { speedCap = 20, maximumDepth = 60 }, cancellation);
        await WaitUntil(admin, state => !state.DeviceMoving && !state.OperationPending, cancellation);
        var attemptId = Guid.NewGuid();
        using var created = await admin.PostAsJsonAsync("/api/v1/admin/invitation", new { attemptId }, cancellation);
        created.EnsureSuccessStatusCode();
        var invitation = await created.Content.ReadFromJsonAsync<Invitation>(cancellationToken: cancellation);
        Assert.NotNull(invitation);
        Assert.StartsWith(guest.BaseAddress.GetLeftPart(UriPartial.Authority) + "/join/", invitation.Url);
        var secret = new Uri(invitation.Url).Fragment["#invite=".Length..];
        var joined = await client.JoinAsync(invitation.SessionId, secret, cancellation);
        Assert.True(joined.Ok, joined.Code);
        var pending = await WaitUntil(admin, state => state.SessionMode == "PendingApproval" && state.ConnectionId is not null, cancellation);
        await Post(admin, "approve", new { connectionId = pending.ConnectionId }, cancellation);
        await Post(admin, "arm", null, cancellation);
        var armed = await WaitUntil(admin, state => state.Mode == "Ready" && state.Grant is not null, cancellation);
        var grant = armed.Grant!;
        var submitted = await client.SubmitInputAsync(new(2, grant.Epoch, grant.PolicyVersion, grant.Id, 1, true, new(0, 12, 10, 20, 50)), cancellation);
        Assert.True(submitted.Ok, submitted.Code);
        await WaitUntil(admin, state => state.Observed is { Enabled: true, Desired.Speed: 12 }, cancellation);
        await Post(admin, "simulator/external-zero", null, cancellation);
        var locked = await WaitUntil(admin, state => state.Mode == "Locked" && !state.DeviceMoving && !state.OperationPending, cancellation);
        Assert.Null(locked.Grant);
        await Post(admin, "unlock", new { lockRevision = locked.LockRevision }, cancellation);
        var paused = await WaitUntil(admin, state => state.Mode == "Paused", cancellation);
        Assert.Null(paused.Grant);
        Assert.Equal(0, paused.Desired.Speed);
        await Post(admin, "end", null, cancellation);
        await client.DisposeAsync();
        await BrowserJourney(admin.BaseAddress, operatorToken!, guest.BaseAddress, cancellation);
    }

    private static async Task BrowserJourney(Uri admin, string operatorToken, Uri guest, CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(typeof(LocalApplicationTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>().Single(attribute => attribute.Key == "RepositoryRoot").Value!);
        var start = new ProcessStartInfo("python")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = root
        };
        start.ArgumentList.Add(Path.Combine(root, "tests", "browser", "prototype_smoke.py"));
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Unable to start browser verification.");
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errors = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new
            {
                adminUrl = $"{admin.GetLeftPart(UriPartial.Authority)}/#operator={operatorToken}",
                guestOrigin = guest.GetLeftPart(UriPartial.Authority)
            }));
            process.StandardInput.Close();
            await process.WaitForExitAsync(cancellationToken);
            Assert.True(process.ExitCode == 0, $"Browser verification failed: {await output} {await errors}");
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
    }

    private static async Task Post(HttpClient client, string operation, object? body, CancellationToken cancellationToken)
    {
        using var response = body is null
            ? await client.PostAsync($"/api/v1/admin/{operation}", null, cancellationToken)
            : await client.PostAsJsonAsync($"/api/v1/admin/{operation}", body, cancellationToken);
        Assert.True(response.IsSuccessStatusCode, $"{operation} returned HTTP {(int)response.StatusCode}.");
    }

    private static async Task<ControlSnapshot> WaitUntil(HttpClient client, Func<ControlSnapshot, bool> predicate, CancellationToken cancellationToken)
    {
        while (true)
        {
            var state = await client.GetFromJsonAsync<ControlSnapshot>("/api/v1/admin/status", cancellationToken);
            Assert.NotNull(state);
            if (predicate(state)) return state;
            await Task.Delay(50, cancellationToken);
        }
    }
}
