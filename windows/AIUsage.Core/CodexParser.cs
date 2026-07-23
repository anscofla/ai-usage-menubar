using System.Text.Json;

namespace AIUsage.Core;

// Observed schema 2026-07-23 (matches macOS CodexProvider): rate_limit is a singular
// object with primary_window/secondary_window — NOT the array form in codex-rs source.
public static class CodexParser
{
    public static (IReadOnlyList<CodexReading>? Readings, string? Error) Parse(string json)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException) { return (null, "codex: invalid JSON"); }

        using (doc)
        {
            // Backstop: Parse promises an error tuple, never an exception — GetDouble can
            // throw FormatException on unrepresentable JSON numbers.
            try { return ParseWindows(doc); }
            catch (Exception) { return (null, "codex: malformed payload"); }
        }
    }

    private static (IReadOnlyList<CodexReading>? Readings, string? Error) ParseWindows(JsonDocument doc)
    {
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("rate_limit", out var rl) ||
                rl.ValueKind != JsonValueKind.Object)
                return (null, "codex: missing rate_limit");

            var readings = new List<CodexReading>();
            foreach (var key in new[] { "primary_window", "secondary_window" })
            {
                if (!rl.TryGetProperty(key, out var w) || w.ValueKind != JsonValueKind.Object) continue;
                if (!w.TryGetProperty("used_percent", out var upEl) || upEl.ValueKind != JsonValueKind.Number)
                    return (null, "codex: window missing used_percent");
                var raw = upEl.GetDouble();
                if (double.IsNaN(raw) || double.IsInfinity(raw) || raw < 0)
                    return (null, "codex: used_percent out of range");
                // AwayFromZero matches Swift's .rounded() — same parity rule as UsageParser.
                var pct = (int)Math.Round(Math.Min(raw, 100), MidpointRounding.AwayFromZero);

                int? windowSeconds = null;
                if (w.TryGetProperty("limit_window_seconds", out var lw) && lw.ValueKind == JsonValueKind.Number &&
                    lw.TryGetInt64(out var lws) && lws is > 0 and <= int.MaxValue)
                    windowSeconds = (int)lws;

                DateTimeOffset? resetsAt = null;
                if (w.TryGetProperty("reset_at", out var ra) && ra.ValueKind == JsonValueKind.Number)
                {
                    var epoch = ra.GetDouble();
                    // sane epoch-seconds window (1970..3000) — keeps FromUnixTimeMilliseconds
                    // from throwing on finite-but-absurd values
                    if (double.IsFinite(epoch) && epoch is > 0 and < 32_503_680_000)
                        resetsAt = DateTimeOffset.FromUnixTimeMilliseconds((long)(epoch * 1000));
                }

                readings.Add(new CodexReading(WindowName(windowSeconds), pct, resetsAt));
            }

            return readings.Count > 0 ? (readings, null) : (null, "codex: no usage windows");
        }
    }

    public static string WindowName(int? seconds) => seconds switch
    {
        604_800 => "Weekly",
        > 0 when seconds % 3600 == 0 => $"{seconds / 3600}h",
        > 0 => $"{seconds / 60}m",
        _ => "Limit",
    };
}
