namespace AIUsage.Core;

public enum Severity { Green, Orange, Red }

public static class Display
{
    public static Severity Severity(int percent) =>
        percent >= 90 ? AIUsage.Core.Severity.Red :
        percent >= 70 ? AIUsage.Core.Severity.Orange : AIUsage.Core.Severity.Green;

    /// Icon number/severity covers BOTH providers: the tray has a single icon, so the
    /// worst limit across Claude and Codex is the one worth warning about.
    public static int CombinedMax(UsageSnapshot snapshot, CodexState codex)
    {
        var max = snapshot.MaxPercent;
        foreach (var r in codex.LastReadings ?? []) max = Math.Max(max, r.Percent);
        return max;
    }

    // 100 renders as "100" (smaller font). "99+" is the documented fallback ONLY if the
    // on-device DPI legibility check fails — do not hard-code it preemptively.
    public static string IconText(TrayState state, CodexState codex) => state switch
    {
        TrayState.Ok ok => CombinedMax(ok.Snapshot, codex).ToString(),
        TrayState.Degraded => "!",
        _ => "…",
    };

    public static string Tooltip(TrayState state, CodexState codex)
    {
        var text = state switch
        {
            TrayState.Ok ok => Summary(ok.Snapshot, codex),
            TrayState.Degraded { Last: not null } d => $"⚠ {d.Reason} · {Summary(d.Last, codex)}",
            TrayState.Degraded d => $"⚠ {d.Reason}",
            _ => "AI Usage: loading…",
        };
        // A failed Codex poll must be visible without opening the menu (stale numbers otherwise
        // masquerade as fresh). Icon stays number-based — Claude degradation already flips it to "!".
        if (codex is CodexState.Degraded) text += " · ⚠ Codex";
        return text.Length <= 127 ? text : text[..127]; // NotifyIcon.Text hard limit (.NET 8+: 127)
    }

    /// Parity with the macOS popup: "resets 7/29, 5d 13h"; under a day "resets 7/29, 3h 12m";
    /// under an hour "resets in 42m". Date is the reset moment in local time.
    public static string ResetCountdown(DateTimeOffset resetsAt, DateTimeOffset now)
    {
        var delta = resetsAt - now;
        if (delta <= TimeSpan.Zero) return "resets now";
        if (delta.TotalHours < 1) return $"resets in {Math.Max(1, delta.Minutes)}m";
        var local = resetsAt.ToLocalTime();
        var date = $"{local.Month}/{local.Day}";
        return delta.TotalDays >= 1
            ? $"resets {date}, {(int)delta.TotalDays}d {delta.Hours}h"
            : $"resets {date}, {(int)delta.TotalHours}h {delta.Minutes}m";
    }

    private static string Summary(UsageSnapshot s, CodexState codex)
    {
        var text = $"Session {s.Session.Percent}% · Weekly {s.WeeklyAll.Percent}% · Model {s.WeeklyScoped.Percent}%";
        if (codex.LastReadings is { Count: > 0 } cr)
            text += $" · Codex {string.Join("/", cr.Select(r => r.Percent))}%";
        return text;
    }
}
