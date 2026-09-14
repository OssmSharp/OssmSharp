using System.Net.Http.Headers;

namespace OSSMDotNet.Web.PrototypeCli;

internal static class AdminClient
{
    public static async Task<int> SendAsync(Uri origin, string operation, HttpMethod method, CancellationToken cancellationToken)
    {
        if (!Console.IsInputRedirected)
        {
            Console.Error.WriteLine("Supply the operator capability on stdin, not in a command-line argument.");
            return 2;
        }
        var token = await Console.In.ReadLineAsync(cancellationToken);
        if (token is not { Length: 43 })
        {
            Console.Error.WriteLine("An operator capability is required.");
            return 2;
        }
        using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false };
        using var client = new HttpClient(handler) { BaseAddress = origin, Timeout = TimeSpan.FromSeconds(3) };
        using var request = new HttpRequestMessage(method, $"/api/v1/admin/{operation}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("Origin", origin.GetLeftPart(UriPartial.Authority));
        try
        {
            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                Console.Error.WriteLine($"Admin request rejected (HTTP {(int)response.StatusCode}).");
                return 1;
            }
            Console.WriteLine(method == HttpMethod.Get
                ? await response.Content.ReadAsStringAsync(cancellationToken)
                : "Stop accepted; use status to observe the simulated outcome.");
            return 0;
        }
        catch (HttpRequestException)
        {
            Console.Error.WriteLine("Admin is unavailable. Start the local application with Aspire.");
            return 1;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Admin request cancelled or timed out; its outcome is not confirmed.");
            return 1;
        }
    }
}
