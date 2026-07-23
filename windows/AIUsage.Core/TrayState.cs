namespace AIUsage.Core;

public abstract record TrayState
{
    public sealed record Loading : TrayState;
    public sealed record Ok(UsageSnapshot Snapshot) : TrayState;
    public sealed record Degraded(string Reason, UsageSnapshot? Last) : TrayState;
}

public static class TrayStateMachine
{
    public static TrayState Next(TrayState current, UsageSnapshot? fetched, string? error)
    {
        if (fetched != null) return new TrayState.Ok(fetched);
        var last = current switch
        {
            TrayState.Ok ok => ok.Snapshot,
            TrayState.Degraded d => d.Last,
            _ => null,
        };
        return new TrayState.Degraded(error ?? "unknown error", last);
    }
}
