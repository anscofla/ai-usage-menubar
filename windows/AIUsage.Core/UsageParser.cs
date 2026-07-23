using System.Text.Json;

namespace AIUsage.Core;

public static class UsageParser
{
    public static (UsageSnapshot? Snapshot, string? Error) Parse(string json)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException) { return (null, "invalid JSON"); }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("limits", out var limits) ||
                limits.ValueKind != JsonValueKind.Array)
                return (null, "missing limits array");

            UsageLimit? session = null, weeklyAll = null, weeklyScoped = null;
            foreach (var el in limits.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object ||
                    !el.TryGetProperty("kind", out var kindEl) || kindEl.ValueKind != JsonValueKind.String)
                    return (null, "limit missing kind");
                var kind = kindEl.GetString();
                if (kind is not ("session" or "weekly_all" or "weekly_scoped"))
                    continue; // unknown kinds tolerated, like macOS version

                if (!el.TryGetProperty("percent", out var pctEl) || pctEl.ValueKind != JsonValueKind.Number)
                    return (null, $"limit '{kind}' missing numeric percent");
                var raw = pctEl.GetDouble();
                if (double.IsNaN(raw) || double.IsInfinity(raw) || raw < 0)
                    return (null, $"limit '{kind}' percent out of range");
                var pct = (int)Math.Round(Math.Min(raw, 100)); // clamp >100, same policy as macOS version

                if (!el.TryGetProperty("resets_at", out var rsEl) || rsEl.ValueKind != JsonValueKind.String ||
                    !DateTimeOffset.TryParse(rsEl.GetString(), out var resetsAt))
                    return (null, $"limit '{kind}' missing/invalid resets_at");

                switch (kind)
                {
                    case "session":
                        if (session != null) return (null, "duplicate session limit");
                        session = new UsageLimit(LimitKind.Session, pct, resetsAt); break;
                    case "weekly_all":
                        if (weeklyAll != null) return (null, "duplicate weekly_all limit");
                        weeklyAll = new UsageLimit(LimitKind.WeeklyAll, pct, resetsAt); break;
                    case "weekly_scoped":
                        if (weeklyScoped != null) return (null, "duplicate weekly_scoped limit");
                        weeklyScoped = new UsageLimit(LimitKind.WeeklyScoped, pct, resetsAt); break;
                }
            }

            if (session == null || weeklyAll == null || weeklyScoped == null)
                return (null, "expected exactly one of each limit kind");
            return (new UsageSnapshot(session, weeklyAll, weeklyScoped), null);
        }
    }
}
