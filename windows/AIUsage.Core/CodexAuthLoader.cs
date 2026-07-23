using System.Text.Json;

namespace AIUsage.Core;

// Deliberately NOT a record — same token-leak reasoning as Credentials.
public sealed class CodexAuth
{
    public string AccessToken { get; }
    public string? AccountId { get; }
    public CodexAuth(string accessToken, string? accountId) { AccessToken = accessToken; AccountId = accountId; }
    public override string ToString() => "CodexAuth(<redacted>)";
}

public static class CodexAuthLoader
{
    public static string DefaultPath()
    {
        var dir = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (string.IsNullOrWhiteSpace(dir))
            dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        return Path.Combine(dir, "auth.json");
    }

    public static (CodexAuth? Auth, string? Error) ParseJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("tokens", out var tokens) ||
                tokens.ValueKind != JsonValueKind.Object ||
                !tokens.TryGetProperty("access_token", out var tok) || tok.ValueKind != JsonValueKind.String)
                return (null, "codex auth missing tokens.access_token");
            var token = tok.GetString();
            if (string.IsNullOrWhiteSpace(token)) return (null, "codex token empty");
            string? account = null;
            if (tokens.TryGetProperty("account_id", out var acc) && acc.ValueKind == JsonValueKind.String)
                account = acc.GetString();
            return (new CodexAuth(token, account), null);
        }
        catch (JsonException) { return (null, "codex auth not valid JSON"); }
    }

    /// Missing file = not logged in (LoggedIn=false, section hidden) — distinct from a read error.
    /// No Exists pre-check: read directly and classify the exception, so a file deleted
    /// between check and read (replace-style rotation) still lands on "not logged in".
    public static (CodexAuth? Auth, string? Error, bool LoggedIn) LoadFromFile(string path)
    {
        try
        {
            var (auth, err) = ParseJson(File.ReadAllText(path));
            return (auth, err, true);
        }
        catch (FileNotFoundException) { return (null, null, false); }
        catch (DirectoryNotFoundException) { return (null, null, false); }
        catch (IOException) { return (null, "codex auth file unreadable", true); }
        catch (UnauthorizedAccessException) { return (null, "codex auth file access denied", true); }
        catch (System.Security.SecurityException) { return (null, "codex auth file access denied", true); }
    }
}
