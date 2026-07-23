namespace AIUsage.Core;

public enum Severity { Green, Orange, Red }

public static class Display
{
    public static Severity Severity(int percent) =>
        percent >= 90 ? AIUsage.Core.Severity.Red :
        percent >= 70 ? AIUsage.Core.Severity.Orange : AIUsage.Core.Severity.Green;

    // 100 renders as "100" (smaller font). "99+" is the documented fallback ONLY if the
    // on-device DPI legibility check fails — do not hard-code it preemptively.
    public static string IconText(TrayState state) => state switch
    {
        TrayState.Ok ok => ok.Snapshot.MaxPercent.ToString(),
        TrayState.Degraded => "!",
        _ => "…",
    };

    public static string Tooltip(TrayState state)
    {
        var text = state switch
        {
            TrayState.Ok ok => Summary(ok.Snapshot),
            TrayState.Degraded { Last: not null } d => $"⚠ {d.Reason} · {Summary(d.Last)}",
            TrayState.Degraded d => $"⚠ {d.Reason}",
            _ => "AI Usage: loading…",
        };
        return text.Length <= 127 ? text : text[..127]; // NotifyIcon.Text hard limit (.NET 8+: 127)
    }

    public static string ResetCountdown(DateTimeOffset resetsAt, DateTimeOffset now)
    {
        var delta = resetsAt - now;
        if (delta <= TimeSpan.Zero) return "resets now";
        return delta.TotalHours >= 1
            ? $"resets in {(int)delta.TotalHours}h {delta.Minutes}m"
            : $"resets in {Math.Max(1, delta.Minutes)}m";
    }

    private static string Summary(UsageSnapshot s) =>
        $"Session {s.Session.Percent}% · Weekly {s.WeeklyAll.Percent}% · Model {s.WeeklyScoped.Percent}%";
}
