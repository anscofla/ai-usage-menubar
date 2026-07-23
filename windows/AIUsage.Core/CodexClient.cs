using System.Net.Http.Headers;

namespace AIUsage.Core;

public sealed class CodexClient
{
    private const string Endpoint = "https://chatgpt.com/backend-api/wham/usage";
    private readonly string _authPath;
    private readonly Func<CodexAuth, CancellationToken, Task<(int Status, string Body)>> _httpGet;

    public CodexClient(string authPath,
        Func<CodexAuth, CancellationToken, Task<(int Status, string Body)>> httpGet)
    {
        _authPath = authPath;
        _httpGet = httpGet;
    }

    public static CodexClient CreateDefault()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15), MaxResponseContentBufferSize = 256 * 1024 };
        return new CodexClient(CodexAuthLoader.DefaultPath(), async (auth, ct) =>
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, Endpoint);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
            req.Headers.UserAgent.ParseAdd("codex-cli");
            if (!string.IsNullOrEmpty(auth.AccountId))
                req.Headers.Add("ChatGPT-Account-Id", auth.AccountId);
            using var resp = await http.SendAsync(req, ct);
            return ((int)resp.StatusCode, await resp.Content.ReadAsStringAsync(ct));
        });
    }

    public async Task<CodexFetchResult> FetchAsync(CancellationToken ct)
    {
        // Reread auth.json every poll (no memory cache) — same account-switch invariant
        // as the Claude client: rotation doesn't 401 the old token.
        var (auth0, authErr0, loggedIn0) = CodexAuthLoader.LoadFromFile(_authPath);
        if (!loggedIn0) return new CodexFetchResult(null, null, false);
        if (auth0 == null) return new CodexFetchResult(null, authErr0, true);

        var result = await GetSafeAsync(auth0, ct);
        if (result.Error != null) return new CodexFetchResult(null, result.Error, true);
        var (status, body) = (result.Status, result.Body);

        if (status == 401)
        {
            var (auth, authErr, loggedIn) = CodexAuthLoader.LoadFromFile(_authPath);
            if (!loggedIn) return new CodexFetchResult(null, null, false);
            if (auth == null) return new CodexFetchResult(null, authErr, true);
            result = await GetSafeAsync(auth, ct);
            if (result.Error != null) return new CodexFetchResult(null, result.Error, true);
            (status, body) = (result.Status, result.Body);
            if (status == 401) return new CodexFetchResult(null, "codex unauthorized (run codex to refresh)", true);
        }

        if (status != 200) return new CodexFetchResult(null, $"codex API HTTP {status}", true);
        var (readings, parseErr) = CodexParser.Parse(body);
        return new CodexFetchResult(readings, parseErr, true);
    }

    private async Task<(int Status, string Body, string? Error)> GetSafeAsync(CodexAuth auth, CancellationToken ct)
    {
        try
        {
            var (status, body) = await _httpGet(auth, ct);
            return (status, body, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return (0, "", "codex request timed out"); }
        catch (HttpRequestException) { return (0, "", "codex network error"); }
        catch (Exception) { return (0, "", "codex unexpected transport error"); }
    }
}
