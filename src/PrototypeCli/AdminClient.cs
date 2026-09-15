namespace OSSMDotNet.Web.PrototypeCli;

internal static class AdminClient
{
    public static async Task<int> SendAsync(Uri origin, string operation, HttpMethod method, CancellationToken cancellationToken)
    {
        using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false };
        using var client = new HttpClient(handler) { BaseAddress = origin, Timeout = TimeSpan.FromSeconds(3) };
        using var request = new HttpRequestMessage(method, $"/api/v1/admin/{operation}");
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
