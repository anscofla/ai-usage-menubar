using System.Net.Http.Headers;

namespace AIUsage.Core;

public sealed class UsageClient
{
    private const string Endpoint = "https://api.anthropic.com/api/oauth/usage";
    private readonly string _credentialsPath;
    private readonly Func<string, CancellationToken, Task<(int Status, string Body)>> _httpGet;
    public UsageClient(string credentialsPath,
        Func<string, CancellationToken, Task<(int Status, string Body)>> httpGet)
    {
        _credentialsPath = credentialsPath;
        _httpGet = httpGet;
    }

    public static UsageClient CreateDefault()
    {
        // 256KB cap: the usage payload is ~1KB; a hostile/broken response must not balloon memory.
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15), MaxResponseContentBufferSize = 256 * 1024 };
        return new UsageClient(CredentialsLoader.DefaultPath(), async (token, ct) =>
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, Endpoint);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Headers.Add("anthropic-beta", "oauth-2025-04-20");
            using var resp = await http.SendAsync(req, ct);
            return ((int)resp.StatusCode, await resp.Content.ReadAsStringAsync(ct));
        });
    }

    public async Task<(UsageSnapshot? Snapshot, string? Error)> FetchAsync(CancellationToken ct)
    {
        // Reread credentials every poll (no memory cache): an account switch rotates the
        // token without invalidating the old one, so a 401 never fires — a fresh read is
        // the only way "Refresh now" picks up the new account.
        var (cred0, credErr0) = ReadCredentials();
        if (cred0 == null) return (null, credErr0);
        var token = cred0.AccessToken;

        int status; string body;
        var result = await GetSafeAsync(token, ct);
        if (result.Error != null) return (null, result.Error);
        (status, body) = (result.Status, result.Body);

        if (status == 401)
        {
            var (cred, credErr) = ReadCredentials();
            if (cred == null) return (null, credErr);
            result = await GetSafeAsync(cred.AccessToken, ct);
            if (result.Error != null) return (null, result.Error);
            (status, body) = (result.Status, result.Body);
            if (status == 401) return (null, "unauthorized (token stale?)");
        }

        if (status != 200) return (null, $"usage API HTTP {status}");
        return UsageParser.Parse(body);
    }

    // TaskCanceledException derives from OperationCanceledException — a plain catch order
    // (OCE first) makes the TCE clause unreachable (CS0160). Use a filtered handler instead:
    // caller-initiated cancellation propagates, HttpClient timeout becomes a degraded reason.
    private async Task<(int Status, string Body, string? Error)> GetSafeAsync(string token, CancellationToken ct)
    {
        try
        {
            var (status, body) = await _httpGet(token, ct);
            return (status, body, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return (0, "", "request timed out"); }
        catch (HttpRequestException) { return (0, "", "network error"); }
        catch (Exception) { return (0, "", "unexpected transport error"); } // no-crash guarantee
    }

    private (Credentials? Cred, string? Error) ReadCredentials() =>
        CredentialsLoader.LoadFromFile(_credentialsPath, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
}
