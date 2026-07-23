namespace AIUsage.Core;

public sealed record CodexReading(string Name, int Percent, DateTimeOffset? ResetsAt);

// Codex is a secondary, optional section: not being logged in is Hidden (not an error),
// and a Codex failure never degrades the Claude side of the tray.
public abstract record CodexState
{
    public sealed record Hidden : CodexState;
    public sealed record Loading : CodexState;
    public sealed record Ok(IReadOnlyList<CodexReading> Readings) : CodexState;
    public sealed record Degraded(string Reason, IReadOnlyList<CodexReading>? Last) : CodexState;

    public IReadOnlyList<CodexReading>? LastReadings => this switch
    {
        Ok ok => ok.Readings,
        Degraded d => d.Last,
        _ => null,
    };
}

public static class CodexStateMachine
{
    public static CodexState Next(CodexState current, CodexFetchResult result)
    {
        if (!result.LoggedIn) return new CodexState.Hidden();
        if (result.Readings != null) return new CodexState.Ok(result.Readings);
        return new CodexState.Degraded(result.Error ?? "unknown error", current.LastReadings);
    }
}

public sealed record CodexFetchResult(IReadOnlyList<CodexReading>? Readings, string? Error, bool LoggedIn);
