using System.Text.Json;

namespace AIUsage.Core;

public sealed record Credentials(string AccessToken, long ExpiresAtMs);

public static class CredentialsLoader
{
    public static string DefaultPath()
    {
        var dir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        if (string.IsNullOrWhiteSpace(dir))
            dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
        return Path.Combine(dir, ".credentials.json");
    }

    public static (Credentials? Credentials, string? Error) ParseJson(string json, long nowMs)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("claudeAiOauth", out var oauth) ||
                oauth.ValueKind != JsonValueKind.Object ||
                !oauth.TryGetProperty("accessToken", out var tok) || tok.ValueKind != JsonValueKind.String ||
                !oauth.TryGetProperty("expiresAt", out var exp) || exp.ValueKind != JsonValueKind.Number ||
                !exp.TryGetInt64(out var expiresAt))
                return (null, "credentials missing claudeAiOauth fields");
            var token = tok.GetString();
            if (string.IsNullOrWhiteSpace(token)) return (null, "credentials token empty");
            if (expiresAt <= nowMs) return (null, "token expired");
            return (new Credentials(token, expiresAt), null);
        }
        catch (JsonException) { return (null, "credentials not valid JSON"); }
    }

    public static (Credentials? Credentials, string? Error) LoadFromFile(string path, long nowMs)
    {
        // One bounded retry covers the credential-replacement race (file briefly missing or locked).
        for (var attempt = 0; ; attempt++)
        {
            string? error;
            try
            {
                if (File.Exists(path)) return ParseJson(File.ReadAllText(path), nowMs);
                error = "credentials file not found";
            }
            catch (IOException) { error = "credentials file unreadable"; }
            catch (UnauthorizedAccessException) { error = "credentials file access denied"; }
            catch (System.Security.SecurityException) { error = "credentials file access denied"; }
            if (attempt >= 1) return (null, error);
            Thread.Sleep(500);
        }
    }
}
