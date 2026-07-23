namespace AIUsage.Core;

public enum LimitKind { Session, WeeklyAll, WeeklyScoped }

public sealed record UsageLimit(LimitKind Kind, int Percent, DateTimeOffset ResetsAt);

public sealed record UsageSnapshot(UsageLimit Session, UsageLimit WeeklyAll, UsageLimit WeeklyScoped)
{
    public int MaxPercent => Math.Max(Session.Percent, Math.Max(WeeklyAll.Percent, WeeklyScoped.Percent));
}
