# AI Usage Tray (Windows) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Windows system-tray app showing Claude usage (session/weekly/model %) — port of the macOS menu bar app, per `docs/DESIGN-windows.md` v2.

**Architecture:** .NET 10 solution under `windows/`: `AIUsage.Core` (cross-platform logic: credentials, API client, parser, state machine — testable on macOS), `AIUsage.Tray` (WinForms NotifyIcon UI, Windows-only), `AIUsage.Tests` (console assert harness, runs on macOS). Release exe built on GitHub Actions windows-latest only.

**Tech Stack:** .NET 10 LTS, WinForms NotifyIcon, System.Text.Json, GDI+ (System.Drawing), GitHub Actions.

## Global Constraints

- macOS dev loop: `dotnet build -p:EnableWindowsTargeting=true` compiles; `dotnet run --project windows/AIUsage.Tests` must pass. Never ship a macOS-built exe.
- Wire field for utilization is **`percent`** (0–100). Valid data = `limits` array only.
- Each of session / weekly_all / weekly_scoped appears **exactly once**, else schema error.
- Security: never put tokens, Authorization headers, or raw API response bodies in logs, error strings, commits, or fixtures. Fixtures = synthetic JSON only.
- No crash on any failure — degrade to `!` icon, keep last good snapshot with a warning row.
- Tooltip truncated to 127 chars before assigning `NotifyIcon.Text`.
- Mutex name `Local\AIUsageTray` (never `Global\`).
- All GDI objects disposed; `HICON` released via `DestroyIcon`.
- Commit after every task (repo `~/Projects/ai-usage-menubar`, branch `main` is fine — solo repo, matches mac v1 workflow).

---

### Task 0: Toolchain + solution scaffold

**Files:**
- Create: `windows/AIUsage.sln` (via `dotnet new sln`)
- Create: `windows/AIUsage.Core/AIUsage.Core.csproj`
- Create: `windows/AIUsage.Tray/AIUsage.Tray.csproj`
- Create: `windows/AIUsage.Tests/AIUsage.Tests.csproj`

**Interfaces:**
- Produces: buildable empty solution; Core+Tests target `net10.0` (mac-runnable), Tray targets `net10.0-windows`.

- [ ] **Step 1: Install .NET SDK on the Mac** (user-visible install; announce before running)

```bash
brew install --cask dotnet-sdk
dotnet --version   # expect 10.x
```

- [ ] **Step 2: Scaffold projects**

```bash
cd ~/Projects/ai-usage-menubar
mkdir -p windows && cd windows
dotnet new sln -n AIUsage --format sln   # .NET 10 defaults to .slnx without --format
dotnet new classlib -n AIUsage.Core -f net10.0
mkdir AIUsage.Tray                        # WinForms template may be absent on the macOS SDK — csproj written by hand in Step 3
dotnet new console -n AIUsage.Tests -f net10.0
rm AIUsage.Core/Class1.cs
```

- [ ] **Step 3: Write Tray csproj by hand** — create `windows/AIUsage.Tray/AIUsage.Tray.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net10.0-windows</TargetFramework>
    <UseWindowsForms>true</UseWindowsForms>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <EnableWindowsTargeting>true</EnableWindowsTargeting>
    <AssemblyName>AIUsageTray</AssemblyName>
    <ApplicationHighDpiMode>PerMonitorV2</ApplicationHighDpiMode>
    <InvariantGlobalization>true</InvariantGlobalization>
  </PropertyGroup>
</Project>
```

Then create a minimal `windows/AIUsage.Tray/Program.cs` (replaced in Task 7 — a WinExe with no Main won't build):

```csharp
namespace AIUsage.Tray;
internal static class Program
{
    [STAThread]
    static void Main() { } // replaced in Task 7
}
```

Wire up the solution (now that the Tray csproj exists):

```bash
cd ~/Projects/ai-usage-menubar/windows
dotnet sln add AIUsage.Core AIUsage.Tray AIUsage.Tests
dotnet add AIUsage.Tray reference AIUsage.Core
dotnet add AIUsage.Tests reference AIUsage.Core
```

- [ ] **Step 4: Verify build + commit**

```bash
cd ~/Projects/ai-usage-menubar/windows
dotnet build -p:EnableWindowsTargeting=true    # expect Build succeeded
cd .. && git add windows && git commit -m "feat(windows): scaffold .NET solution (Core/Tray/Tests)"
```

---

### Task 1: Core types + usage parser

**Files:**
- Create: `windows/AIUsage.Core/UsageTypes.cs`
- Create: `windows/AIUsage.Core/UsageParser.cs`
- Create: `windows/AIUsage.Tests/Program.cs` (harness skeleton + parser tests)

**Interfaces:**
- Produces: `UsageLimit(LimitKind Kind, int Percent, DateTimeOffset ResetsAt)`, `UsageSnapshot(Session, WeeklyAll, WeeklyScoped)` with `int MaxPercent`, `UsageParser.Parse(string json) -> (UsageSnapshot? snapshot, string? error)`. Error strings are short machine-safe reasons (no response echo).

- [ ] **Step 1: Write harness + failing tests** — `windows/AIUsage.Tests/Program.cs`:

```csharp
using AIUsage.Core;

internal static class TestHarness
{
    private static int _failures;
    public static void Check(bool cond, string name)
    {
        if (cond) { Console.WriteLine($"PASS {name}"); }
        else { _failures++; Console.WriteLine($"FAIL {name}"); }
    }

    public static int Main()
    {
        ParserTests.Run();
        Console.WriteLine(_failures == 0 ? "ALL PASS" : $"{_failures} FAILURES");
        return _failures == 0 ? 0 : 1;
    }
}

internal static class ParserTests
{
    private const string Good = """
    {"limits":[
      {"kind":"session","percent":64,"resets_at":"2026-07-23T10:00:00.123456+00:00"},
      {"kind":"weekly_all","percent":79,"resets_at":"2026-07-28T00:00:00Z"},
      {"kind":"weekly_scoped","percent":80,"resets_at":"2026-07-28T00:00:00Z"}
    ]}
    """;

    public static void Run()
    {
        var (snap, err) = UsageParser.Parse(Good);
        TestHarness.Check(err == null && snap != null, "parse: good payload ok");
        TestHarness.Check(snap!.Session.Percent == 64, "parse: session percent");
        TestHarness.Check(snap.WeeklyScoped.Percent == 80, "parse: scoped percent");
        TestHarness.Check(snap.MaxPercent == 80, "parse: max percent");
        TestHarness.Check(snap.Session.ResetsAt.UtcDateTime.Hour == 10, "parse: fractional-seconds ISO8601");

        (_, err) = UsageParser.Parse("""{"limits":[]}""");
        TestHarness.Check(err != null, "parse: empty limits = schema error");

        (_, err) = UsageParser.Parse(Good.Replace("weekly_scoped", "weekly_bogus"));
        TestHarness.Check(err != null, "parse: missing scoped = schema error");

        var dupScoped = Good.Replace("]}", ""","{"kind":"weekly_scoped","percent":10,"resets_at":"2026-07-28T00:00:00Z"}]}""");
        (_, err) = UsageParser.Parse(dupScoped);
        TestHarness.Check(err != null, "parse: duplicate scoped = schema error");

        (_, err) = UsageParser.Parse("not json");
        TestHarness.Check(err != null, "parse: invalid json = error");

        (_, err) = UsageParser.Parse("""{"limits":[{"kind":"session","percent":"64","resets_at":"2026-07-28T00:00:00Z"}]}""");
        TestHarness.Check(err != null, "parse: non-numeric percent = error");

        (_, err) = UsageParser.Parse(Good.Replace("\"percent\":64", "\"percent\":-1"));
        TestHarness.Check(err != null, "parse: negative percent = error");

        (snap, err) = UsageParser.Parse(Good.Replace("\"percent\":64", "\"percent\":150"));
        TestHarness.Check(err == null && snap!.Session.Percent == 100, "parse: >100 clamped to 100");

        (snap, err) = UsageParser.Parse(Good.Replace("\"percent\":64", "\"percent\":64.6"));
        TestHarness.Check(err == null && snap!.Session.Percent == 65, "parse: decimal rounds");
    }
}
```

- [ ] **Step 2: Run to verify failure**

```bash
cd ~/Projects/ai-usage-menubar/windows && dotnet run --project AIUsage.Tests
```
Expected: compile error (`UsageParser` not defined).

- [ ] **Step 3: Implement types** — `windows/AIUsage.Core/UsageTypes.cs`:

```csharp
namespace AIUsage.Core;

public enum LimitKind { Session, WeeklyAll, WeeklyScoped }

public sealed record UsageLimit(LimitKind Kind, int Percent, DateTimeOffset ResetsAt);

public sealed record UsageSnapshot(UsageLimit Session, UsageLimit WeeklyAll, UsageLimit WeeklyScoped)
{
    public int MaxPercent => Math.Max(Session.Percent, Math.Max(WeeklyAll.Percent, WeeklyScoped.Percent));
}
```

- [ ] **Step 4: Implement parser** — `windows/AIUsage.Core/UsageParser.cs`:

```csharp
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
            if (!doc.RootElement.TryGetProperty("limits", out var limits) ||
                limits.ValueKind != JsonValueKind.Array)
                return (null, "missing limits array");

            UsageLimit? session = null, weeklyAll = null, weeklyScoped = null;
            foreach (var el in limits.EnumerateArray())
            {
                if (!el.TryGetProperty("kind", out var kindEl) || kindEl.ValueKind != JsonValueKind.String)
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
```

- [ ] **Step 5: Run tests → all PASS, then commit**

```bash
dotnet run --project AIUsage.Tests   # expect ALL PASS, exit 0
cd .. && git add windows && git commit -m "feat(windows): usage types + parser with assert harness"
```

---

### Task 2: Credentials loader

**Files:**
- Create: `windows/AIUsage.Core/CredentialsLoader.cs`
- Modify: `windows/AIUsage.Tests/Program.cs` (add `CredentialsTests.Run();` to Main and the class below)

**Interfaces:**
- Produces: `Credentials(string AccessToken, long ExpiresAtMs)`; `CredentialsLoader.DefaultPath() -> string`; `CredentialsLoader.ParseJson(string json, long nowMs) -> (Credentials?, string? Error)`; `CredentialsLoader.LoadFromFile(string path, long nowMs) -> (Credentials?, string? Error)` (one 500ms retry on IO failure).

- [ ] **Step 1: Add failing tests** (append class; call from Main):

```csharp
internal static class CredentialsTests
{
    public static void Run()
    {
        const long now = 1_800_000_000_000; // synthetic epoch ms
        var good = """{"claudeAiOauth":{"accessToken":"synthetic-token","expiresAt":1900000000000}}""";
        var (cred, err) = AIUsage.Core.CredentialsLoader.ParseJson(good, now);
        TestHarness.Check(err == null && cred!.AccessToken == "synthetic-token", "cred: parse ok");

        (cred, err) = AIUsage.Core.CredentialsLoader.ParseJson(
            """{"claudeAiOauth":{"accessToken":"synthetic-token","expiresAt":1700000000000}}""", now);
        TestHarness.Check(cred == null && err == "token expired", "cred: expired detected");

        (cred, err) = AIUsage.Core.CredentialsLoader.ParseJson("{}", now);
        TestHarness.Check(cred == null && err != null, "cred: missing oauth block = error");

        (cred, err) = AIUsage.Core.CredentialsLoader.ParseJson("nope", now);
        TestHarness.Check(cred == null && err != null, "cred: invalid json = error");

        (cred, err) = AIUsage.Core.CredentialsLoader.ParseJson(
            """{"claudeAiOauth":{"accessToken":"","expiresAt":1900000000000}}""", now);
        TestHarness.Check(cred == null && err == "credentials token empty", "cred: empty token rejected");

        (_, err) = AIUsage.Core.CredentialsLoader.LoadFromFile(
            Path.Combine(Path.GetTempPath(), "aiusage-definitely-missing.json"), now);
        TestHarness.Check(err == "credentials file not found", "cred: missing file reason (after 1 retry)");
    }
}
```

- [ ] **Step 2: Run → compile failure expected.**

- [ ] **Step 3: Implement** — `windows/AIUsage.Core/CredentialsLoader.cs`:

```csharp
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
            if (!doc.RootElement.TryGetProperty("claudeAiOauth", out var oauth) ||
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
            string? error = null;
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
```

- [ ] **Step 4: Run tests → ALL PASS; commit** `feat(windows): credentials loader (CLAUDE_CONFIG_DIR aware, expiry check)`

---

### Task 3: Tray state machine + display helpers

**Files:**
- Create: `windows/AIUsage.Core/TrayState.cs`
- Create: `windows/AIUsage.Core/Display.cs`
- Modify: `windows/AIUsage.Tests/Program.cs` (add `StateTests.Run(); DisplayTests.Run();`)

**Interfaces:**
- Produces: `TrayState` variants `TrayState.Loading`, `TrayState.Ok(UsageSnapshot)`, `TrayState.Degraded(string Reason, UsageSnapshot? Last)`; `TrayStateMachine.Next(TrayState current, UsageSnapshot? fetched, string? error) -> TrayState`; `Display.Severity(int percent) -> Severity {Green,Orange,Red}`; `Display.Tooltip(TrayState) -> string` (≤127 chars); `Display.IconText(TrayState) -> string` ("64", "99+", "!"); `Display.ResetCountdown(DateTimeOffset resetsAt, DateTimeOffset now) -> string` ("resets in 3h 12m").

- [ ] **Step 1: Failing tests**:

```csharp
internal static class StateTests
{
    private static AIUsage.Core.UsageSnapshot Snap(int s, int w, int m)
    {
        var t = DateTimeOffset.UtcNow.AddHours(3);
        return new AIUsage.Core.UsageSnapshot(
            new(AIUsage.Core.LimitKind.Session, s, t),
            new(AIUsage.Core.LimitKind.WeeklyAll, w, t),
            new(AIUsage.Core.LimitKind.WeeklyScoped, m, t));
    }

    public static void Run()
    {
        var ok = AIUsage.Core.TrayStateMachine.Next(new AIUsage.Core.TrayState.Loading(), Snap(64, 79, 80), null);
        TestHarness.Check(ok is AIUsage.Core.TrayState.Ok, "state: loading→ok");

        var first = ((AIUsage.Core.TrayState.Ok)ok).Snapshot;
        var deg = AIUsage.Core.TrayStateMachine.Next(ok, null, "http 500");
        TestHarness.Check(deg is AIUsage.Core.TrayState.Degraded d1 && d1.Last == first, "state: failure keeps exact last snapshot");

        var deg2 = AIUsage.Core.TrayStateMachine.Next(deg, null, "timeout");
        TestHarness.Check(deg2 is AIUsage.Core.TrayState.Degraded d2 && d2.Last == first, "state: repeated failure still keeps exact last");

        var early = AIUsage.Core.TrayStateMachine.Next(new AIUsage.Core.TrayState.Loading(), null, "no file");
        TestHarness.Check(early is AIUsage.Core.TrayState.Degraded { Last: null }, "state: failure before first data = no last");

        var back = AIUsage.Core.TrayStateMachine.Next(deg2, Snap(1, 2, 3), null);
        TestHarness.Check(back is AIUsage.Core.TrayState.Ok, "state: recovery");
    }
}

internal static class DisplayTests
{
    public static void Run()
    {
        TestHarness.Check(AIUsage.Core.Display.Severity(69) == AIUsage.Core.Severity.Green, "disp: 69 green");
        TestHarness.Check(AIUsage.Core.Display.Severity(70) == AIUsage.Core.Severity.Orange, "disp: 70 orange");
        TestHarness.Check(AIUsage.Core.Display.Severity(90) == AIUsage.Core.Severity.Red, "disp: 90 red");

        var t = DateTimeOffset.UtcNow.AddHours(3);
        var snap = new AIUsage.Core.UsageSnapshot(
            new(AIUsage.Core.LimitKind.Session, 64, t),
            new(AIUsage.Core.LimitKind.WeeklyAll, 79, t),
            new(AIUsage.Core.LimitKind.WeeklyScoped, 100, t));
        TestHarness.Check(AIUsage.Core.Display.IconText(new AIUsage.Core.TrayState.Ok(snap)) == "100", "disp: 100 renders '100'");
        TestHarness.Check(AIUsage.Core.Display.IconText(new AIUsage.Core.TrayState.Degraded("x", null)) == "!", "disp: degraded '!'");

        var longReason = new string('x', 300);
        var tip = AIUsage.Core.Display.Tooltip(new AIUsage.Core.TrayState.Degraded(longReason, snap));
        var expectedFull = $"⚠ {longReason} · Session 64% · Weekly 79% · Model 100%";
        TestHarness.Check(tip == expectedFull[..127], "disp: tooltip is exact 127-char prefix");

        var t0 = new DateTimeOffset(2026, 7, 23, 10, 0, 0, TimeSpan.Zero);
        TestHarness.Check(AIUsage.Core.Display.ResetCountdown(t0.AddHours(3).AddMinutes(12), t0) == "resets in 3h 12m", "disp: countdown h+m");
        TestHarness.Check(AIUsage.Core.Display.ResetCountdown(t0.AddMinutes(59).AddSeconds(59), t0) == "resets in 59m", "disp: countdown 59m59s");
        TestHarness.Check(AIUsage.Core.Display.ResetCountdown(t0.AddHours(1), t0) == "resets in 1h 0m", "disp: countdown exactly 1h");
        TestHarness.Check(AIUsage.Core.Display.ResetCountdown(t0, t0) == "resets now", "disp: countdown at reset");
        TestHarness.Check(AIUsage.Core.Display.ResetCountdown(t0.AddMinutes(-5), t0) == "resets now", "disp: countdown past reset");
        TestHarness.Check(AIUsage.Core.Display.ResetCountdown(t0.AddDays(2), t0) == "resets in 48h 0m", "disp: countdown multi-day hours");
    }
}
```

- [ ] **Step 2: Run → compile failure expected.**

- [ ] **Step 3: Implement** — `windows/AIUsage.Core/TrayState.cs`:

```csharp
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
```

`windows/AIUsage.Core/Display.cs`:

```csharp
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
        return text.Length <= 127 ? text : text[..127];
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
```

- [ ] **Step 4: Run tests → ALL PASS; commit** `feat(windows): tray state machine + display helpers`

---

### Task 4: Usage client (HTTP + 401 reread-retry)

**Files:**
- Create: `windows/AIUsage.Core/UsageClient.cs`
- Modify: `windows/AIUsage.Tests/Program.cs` (add `ClientTests.Run().GetAwaiter().GetResult();`)

**Interfaces:**
- Consumes: `CredentialsLoader.LoadFromFile`, `UsageParser.Parse`.
- Produces: `UsageClient(string credentialsPath, Func<string, CancellationToken, Task<(int Status, string Body)>> httpGet)` — httpGet injected for tests; real transport provided by static factory `UsageClient.CreateDefault()`. Method: `Task<(UsageSnapshot? Snapshot, string? Error)> FetchAsync(CancellationToken ct)`. Caches token; on 401 rereads credentials and retries exactly once.

- [ ] **Step 1: Failing tests** (inject fake transport; no real network):

```csharp
internal static class ClientTests
{
    private const string GoodBody = """
    {"limits":[
      {"kind":"session","percent":10,"resets_at":"2026-07-28T00:00:00Z"},
      {"kind":"weekly_all","percent":20,"resets_at":"2026-07-28T00:00:00Z"},
      {"kind":"weekly_scoped","percent":30,"resets_at":"2026-07-28T00:00:00Z"}
    ]}
    """;

    public static async Task Run()
    {
        var credPath = Path.Combine(Path.GetTempPath(), $"aiusage-test-{Environment.ProcessId}.json");
        await File.WriteAllTextAsync(credPath,
            """{"claudeAiOauth":{"accessToken":"synthetic-token","expiresAt":9999999999999}}""");
        try
        {
            var calls = 0;
            var client = new AIUsage.Core.UsageClient(credPath, (token, ct) =>
            { calls++; return Task.FromResult((200, GoodBody)); });
            var (snap, err) = await client.FetchAsync(CancellationToken.None);
            TestHarness.Check(err == null && snap!.Session.Percent == 10, "client: 200 ok");
            TestHarness.Check(calls == 1, "client: single call on success");

            calls = 0;
            client = new AIUsage.Core.UsageClient(credPath, (token, ct) =>
            { calls++; return Task.FromResult(calls == 1 ? (401, "") : (200, GoodBody)); });
            (snap, err) = await client.FetchAsync(CancellationToken.None);
            TestHarness.Check(err == null && snap != null, "client: 401 → reread + retry succeeds");
            TestHarness.Check(calls == 2, "client: exactly one retry");

            client = new AIUsage.Core.UsageClient(credPath, (token, ct) => Task.FromResult((401, "")));
            (snap, err) = await client.FetchAsync(CancellationToken.None);
            TestHarness.Check(snap == null && err == "unauthorized (token stale?)", "client: persistent 401 degrades");

            client = new AIUsage.Core.UsageClient(credPath, (token, ct) => Task.FromResult((500, "boom")));
            (_, err) = await client.FetchAsync(CancellationToken.None);
            TestHarness.Check(err == "usage API HTTP 500", "client: 5xx reason has no body echo");

            client = new AIUsage.Core.UsageClient(credPath + ".missing", (token, ct) => Task.FromResult((200, GoodBody)));
            (_, err) = await client.FetchAsync(CancellationToken.None);
            TestHarness.Check(err == "credentials file not found", "client: missing creds short-circuits");

            // timeout: HttpClient timeout surfaces as OCE without caller cancellation
            client = new AIUsage.Core.UsageClient(credPath,
                (token, ct) => throw new TaskCanceledException());
            (_, err) = await client.FetchAsync(CancellationToken.None);
            TestHarness.Check(err == "request timed out", "client: timeout reason");

            // token cached across successful polls (credentials read once)
            var tokensSeen = new List<string>();
            client = new AIUsage.Core.UsageClient(credPath, (token, ct) =>
            { tokensSeen.Add(token); return Task.FromResult((200, GoodBody)); });
            await client.FetchAsync(CancellationToken.None);
            await File.WriteAllTextAsync(credPath,
                """{"claudeAiOauth":{"accessToken":"rotated-token","expiresAt":9999999999999}}""");
            await client.FetchAsync(CancellationToken.None);
            TestHarness.Check(tokensSeen is ["synthetic-token", "synthetic-token"], "client: token cached between polls");

            // 401 rereads the rotated file and passes the NEW token to the retry
            tokensSeen.Clear();
            client = new AIUsage.Core.UsageClient(credPath, (token, ct) =>
            { tokensSeen.Add(token); return Task.FromResult(tokensSeen.Count == 1 ? (401, "") : (200, GoodBody)); });
            (snap, err) = await client.FetchAsync(CancellationToken.None);
            TestHarness.Check(err == null && tokensSeen is [_, "rotated-token"], "client: 401 retry uses reread token");
        }
        finally { File.Delete(credPath); }
    }
}
```

- [ ] **Step 2: Run → compile failure expected.**

- [ ] **Step 3: Implement** — `windows/AIUsage.Core/UsageClient.cs`:

```csharp
using System.Net.Http.Headers;

namespace AIUsage.Core;

public sealed class UsageClient
{
    private const string Endpoint = "https://api.anthropic.com/api/oauth/usage";
    private readonly string _credentialsPath;
    private readonly Func<string, CancellationToken, Task<(int Status, string Body)>> _httpGet;
    private string? _cachedToken;

    public UsageClient(string credentialsPath,
        Func<string, CancellationToken, Task<(int Status, string Body)>> httpGet)
    {
        _credentialsPath = credentialsPath;
        _httpGet = httpGet;
    }

    public static UsageClient CreateDefault()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        return new UsageClient(CredentialsLoader.DefaultPath(), async (token, ct) =>
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, Endpoint);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Headers.Add("anthropic-beta", "oauth-2025-04-20");
            using var resp = await http.SendAsync(req, ct);
            return ((int)resp.StatusCode, await resp.Content.ReadAsStringAsync(ct));
        });
    }

    public async Task<(UsageSnapshot? Snapshot, string? Error)> FetchAsync(CancellationToken ct)
    {
        var token = _cachedToken;
        if (token == null)
        {
            var (cred, credErr) = ReadCredentials();
            if (cred == null) return (null, credErr);
            token = _cachedToken = cred.AccessToken;
        }

        int status; string body;
        var result = await GetSafeAsync(token, ct);
        if (result.Error != null) return (null, result.Error);
        (status, body) = (result.Status, result.Body);

        if (status == 401)
        {
            _cachedToken = null;
            var (cred, credErr) = ReadCredentials();
            if (cred == null) return (null, credErr);
            _cachedToken = cred.AccessToken;
            result = await GetSafeAsync(cred.AccessToken, ct);
            if (result.Error != null) return (null, result.Error);
            (status, body) = (result.Status, result.Body);
            if (status == 401) return (null, "unauthorized (token stale?)");
        }

        if (status != 200) return (null, $"usage API HTTP {status}");
        return UsageParser.Parse(body);
    }

    // TaskCanceledException derives from OperationCanceledException — a plain catch order
    // (OCE first) makes the TCE clause unreachable (CS0160). Use a filtered handler instead:
    // caller-initiated cancellation propagates, HttpClient timeout becomes a degraded reason.
    private async Task<(int Status, string Body, string? Error)> GetSafeAsync(string token, CancellationToken ct)
    {
        try
        {
            var (status, body) = await _httpGet(token, ct);
            return (status, body, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return (0, "", "request timed out"); }
        catch (HttpRequestException) { return (0, "", "network error"); }
        catch (Exception) { return (0, "", "unexpected transport error"); } // no-crash guarantee
    }

    private (Credentials? Cred, string? Error) ReadCredentials() =>
        CredentialsLoader.LoadFromFile(_credentialsPath, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
}
```

- [ ] **Step 4: Run tests → ALL PASS; commit** `feat(windows): usage client with injected transport + 401 reread-retry`

---

### Task 5: Icon renderer (Windows-only; compile-checked on mac)

**Files:**
- Create: `windows/AIUsage.Tray/IconRenderer.cs`
- Create: `windows/AIUsage.Tray/NativeMethods.cs`

**Interfaces:**
- Consumes: `Display.IconText`, `Display.Severity`, `TrayState`.
- Produces: `RenderedIcon : IDisposable` with `Icon Icon`; `IconRenderer.Render(TrayState state, int sizePx) -> RenderedIcon`. Caller keeps previous `RenderedIcon` alive until `NotifyIcon.Icon` is swapped, then disposes it.

- [ ] **Step 1: Implement** — `windows/AIUsage.Tray/NativeMethods.cs`:

```csharp
using System.Runtime.InteropServices;

namespace AIUsage.Tray;

internal static partial class NativeMethods
{
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DestroyIcon(IntPtr hIcon);
}
```

`windows/AIUsage.Tray/IconRenderer.cs`:

```csharp
using System.Drawing;
using System.Drawing.Text;
using AIUsage.Core;

namespace AIUsage.Tray;

public sealed class RenderedIcon : IDisposable
{
    private readonly IntPtr _hIcon;
    public Icon Icon { get; }

    internal RenderedIcon(Icon icon, IntPtr hIcon) { Icon = icon; _hIcon = hIcon; }

    public void Dispose()
    {
        Icon.Dispose();
        if (_hIcon != IntPtr.Zero) NativeMethods.DestroyIcon(_hIcon);
    }
}

public static class IconRenderer
{
    public static RenderedIcon Render(TrayState state, int sizePx)
    {
        var text = Display.IconText(state);
        var back = state switch
        {
            TrayState.Ok ok => Display.Severity(ok.Snapshot.MaxPercent) switch
            {
                Severity.Red => Color.FromArgb(200, 40, 40),
                Severity.Orange => Color.FromArgb(220, 130, 20),
                _ => Color.FromArgb(30, 140, 60),
            },
            TrayState.Degraded => Color.FromArgb(110, 110, 110),
            _ => Color.FromArgb(70, 70, 70),
        };

        using var bmp = new Bitmap(sizePx, sizePx);
        using (var g = Graphics.FromImage(bmp))
        {
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using var brush = new SolidBrush(back);
            g.FillEllipse(brush, 0, 0, sizePx - 1, sizePx - 1);

            var fontSize = text.Length switch { <= 1 => sizePx * 0.62f, 2 => sizePx * 0.52f, _ => sizePx * 0.40f };
            using var font = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Pixel);
            using var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(text, font, Brushes.White, new RectangleF(0, 0.5f, sizePx, sizePx), fmt);
        }

        var hIcon = bmp.GetHicon();
        return new RenderedIcon((Icon)Icon.FromHandle(hIcon).Clone(), DisposeTemp(hIcon));
    }

    // Icon.FromHandle doesn't own the HICON; we clone (owned copy) and destroy the original now.
    private static IntPtr DisposeTemp(IntPtr hIcon)
    {
        NativeMethods.DestroyIcon(hIcon);
        return IntPtr.Zero;
    }
}
```

- [ ] **Step 2: Compile check + commit**

```bash
cd ~/Projects/ai-usage-menubar/windows && dotnet build -p:EnableWindowsTargeting=true
cd .. && git add windows && git commit -m "feat(windows): DPI-sized GDI icon renderer with owned HICON lifecycle"
```

---

### Task 6: Autostart (registry + stable copy)

**Files:**
- Create: `windows/AIUsage.Tray/Autostart.cs`

**Interfaces:**
- Produces: `Autostart.IsEnabled() -> bool`; `Autostart.Enable() -> string?` (error or null); `Autostart.Disable() -> string?`. Run value name `"AIUsageTray"`; install dir `%LOCALAPPDATA%\AIUsageTray`.

- [ ] **Step 1: Implement** — `windows/AIUsage.Tray/Autostart.cs`:

```csharp
using Microsoft.Win32;

namespace AIUsage.Tray;

public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "AIUsageTray";

    private static string InstallDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AIUsageTray");
    private static string InstalledExe => Path.Combine(InstallDir, "AIUsageTray.exe");

    private static string ExpectedRunValue => $"\"{InstalledExe}\"";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            // Exact match on our quoted install path only — a foreign/malformed value counts as disabled.
            return key?.GetValue(ValueName) is string v &&
                   string.Equals(v, ExpectedRunValue, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return false; // called at startup — must never crash
        }
    }

    public static string? Enable()
    {
        try
        {
            var current = Environment.ProcessPath!;
            if (!string.Equals(current, InstalledExe, StringComparison.OrdinalIgnoreCase))
            {
                Directory.CreateDirectory(InstallDir);
                File.Copy(current, InstalledExe, overwrite: true);
            }
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            key.SetValue(ValueName, ExpectedRunValue);
            return null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            return "autostart registration failed (policy or file access)";
        }
    }

    public static string? Disable()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            // Delete only if the value is exactly ours — never remove a foreign installation's entry.
            if (key?.GetValue(ValueName) is string v &&
                string.Equals(v, ExpectedRunValue, StringComparison.OrdinalIgnoreCase))
                key.DeleteValue(ValueName);
            return null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            return "autostart removal failed (policy)";
        }
    }
}
```

- [ ] **Step 2: Compile check + commit** `feat(windows): autostart via HKCU Run + LOCALAPPDATA self-copy`

---

### Task 7: Tray application context + Program (mutex, single-flight polling)

**Files:**
- Create: `windows/AIUsage.Tray/TrayAppContext.cs`
- Modify: `windows/AIUsage.Tray/Program.cs` (replace Task-0 stub)

**Interfaces:**
- Consumes: `UsageClient.CreateDefault().FetchAsync`, `TrayStateMachine.Next`, `Display.*`, `IconRenderer.Render`, `Autostart.*`.
- Produces: the runnable app.

- [ ] **Step 1: Implement** — `windows/AIUsage.Tray/TrayAppContext.cs`:

```csharp
using AIUsage.Core;

namespace AIUsage.Tray;

public sealed class TrayAppContext : ApplicationContext
{
    private readonly NotifyIcon _notify = new() { Visible = true };
    private readonly UsageClient _client = UsageClient.CreateDefault();
    private readonly CancellationTokenSource _cts = new();
    private readonly SynchronizationContext _ui;
    private TrayState _state = new TrayState.Loading();
    private RenderedIcon? _currentIcon;
    private Task? _inFlight;

    public TrayAppContext()
    {
        // SynchronizationContext.Current is still null here (no control created, message loop
        // not started) — install the WinForms context explicitly instead of capturing null.
        if (SynchronizationContext.Current is not WindowsFormsSynchronizationContext)
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        _ui = SynchronizationContext.Current!;

        BuildMenu();
        ApplyState(_state);
        // Re-render the icon when display scale/monitor changes (DPI checklist item).
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
        _pollTask = PollLoopAsync(); // retained so faults are observable, not fire-and-forget
    }

    private Task? _pollTask;

    private void OnDisplayChanged(object? sender, EventArgs e) =>
        _ui.Post(_ => ApplyState(_state), null);

    private async Task PollLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            await RefreshOnceAsync();
            try { await Task.Delay(TimeSpan.FromSeconds(60), _cts.Token); }
            catch (OperationCanceledException) { return; }
        }
    }

    private Task RefreshOnceAsync()
    {
        if (_inFlight is { IsCompleted: false }) return _inFlight; // single-flight: join
        _inFlight = DoRefreshAsync();
        return _inFlight;
    }

    private async Task DoRefreshAsync()
    {
        UsageSnapshot? snap; string? err;
        try { (snap, err) = await _client.FetchAsync(_cts.Token); }
        catch (OperationCanceledException) { return; }
        catch (Exception) { (snap, err) = (null, "unexpected error"); } // no-crash: poll loop must never fault
        var next = TrayStateMachine.Next(_state, snap, err);
        _ui.Post(_ =>
        {
            try { _state = next; ApplyState(next); BuildMenu(); }
            catch (Exception) { /* rendering failure must not kill the UI thread */ }
        }, null);
    }

    private void ApplyState(TrayState state)
    {
        var size = Math.Max(SystemInformation.SmallIconSize.Width, SystemInformation.SmallIconSize.Height);
        var rendered = IconRenderer.Render(state, size);
        _notify.Icon = rendered.Icon;
        _notify.Text = Display.Tooltip(state);
        _currentIcon?.Dispose();
        _currentIcon = rendered;
    }

    private void BuildMenu()
    {
        var menu = new ContextMenuStrip();
        var now = DateTimeOffset.UtcNow;

        var snapshot = _state switch
        {
            TrayState.Ok ok => ok.Snapshot,
            TrayState.Degraded d => d.Last,
            _ => null,
        };
        if (_state is TrayState.Degraded deg)
            menu.Items.Add(new ToolStripMenuItem($"⚠ {deg.Reason}") { Enabled = false });
        if (_state is TrayState.Loading)
            menu.Items.Add(new ToolStripMenuItem("Loading…") { Enabled = false });

        if (snapshot != null)
        {
            AddLimitRow(menu, "Session", snapshot.Session, now);
            AddLimitRow(menu, "Weekly", snapshot.WeeklyAll, now);
            AddLimitRow(menu, "Model", snapshot.WeeklyScoped, now);
        }

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Refresh now", null, (_, _) => _ = RefreshOnceAsync());

        var auto = new ToolStripMenuItem("Start at login") { Checked = Autostart.IsEnabled() };
        auto.Click += (_, _) =>
        {
            var err = auto.Checked ? Autostart.Disable() : Autostart.Enable();
            if (err != null) auto.Text = $"Start at login — {err}";
            auto.Checked = Autostart.IsEnabled();
        };
        menu.Items.Add(auto);
        menu.Items.Add("Quit", null, (_, _) => ExitApp());

        var old = _notify.ContextMenuStrip;
        _notify.ContextMenuStrip = menu;
        old?.Dispose();
    }

    private static void AddLimitRow(ContextMenuStrip menu, string label, UsageLimit limit, DateTimeOffset now) =>
        menu.Items.Add(new ToolStripMenuItem(
            $"{label}  {limit.Percent}%  ·  {Display.ResetCountdown(limit.ResetsAt, now)}") { Enabled = false });

    private void ExitApp()
    {
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
        _cts.Cancel();
        _notify.Visible = false;
        _notify.Dispose();
        _currentIcon?.Dispose();
        ExitThread();
    }
}
```

- [ ] **Step 2: Replace Program.cs**:

```csharp
namespace AIUsage.Tray;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        using var mutex = new Mutex(initiallyOwned: true, @"Local\AIUsageTray", out var createdNew);
        if (!createdNew) return; // another instance in this session — exit quietly

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayAppContext());
        GC.KeepAlive(mutex);
    }
}
```

Note: left-click opening the menu — WinForms shows `ContextMenuStrip` on right-click natively; add in `TrayAppContext` constructor after `BuildMenu()`:

```csharp
// Unsupported private API — guard the lookup so a runtime servicing change degrades
// left-click to a no-op (right-click still works natively) instead of crashing.
var showMenu = typeof(NotifyIcon).GetMethod("ShowContextMenu",
    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
_notify.MouseUp += (_, e) =>
{
    if (e.Button == MouseButtons.Left && showMenu != null)
        try { showMenu.Invoke(_notify, null); } catch (Exception) { }
};
```

- [ ] **Step 3: Full build + tests + commit**

```bash
cd ~/Projects/ai-usage-menubar/windows
dotnet build -p:EnableWindowsTargeting=true && dotnet run --project AIUsage.Tests
cd .. && git add windows && git commit -m "feat(windows): tray app context, single-flight polling, Local mutex"
```

---

### Task 8: CI (windows-latest publish) + README + pre-release

**Files:**
- Create: `.github/workflows/windows-build.yml`
- Modify: `README.md` (add Windows section), `README.ko.md` (same in Korean)

- [ ] **Step 1: Workflow** — `.github/workflows/windows-build.yml`:

```yaml
name: windows-build
on:
  push:
    paths: ["windows/**", ".github/workflows/windows-build.yml"]
  workflow_dispatch:

jobs:
  build:
    runs-on: windows-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: "10.0.x"
      - name: Test
        run: dotnet run --project windows/AIUsage.Tests
      - name: Publish
        run: >
          dotnet publish windows/AIUsage.Tray -c Release -r win-x64 --self-contained
          -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
          -o publish
      - name: Package and checksum (zip built and hashed on the runner — the hash must cover the exact asset users download)
        shell: pwsh
        run: |
          Compress-Archive -Path publish/AIUsageTray.exe -DestinationPath AIUsageTray-win-x64.zip
          (Get-FileHash AIUsageTray-win-x64.zip -Algorithm SHA256).Hash + "  AIUsageTray-win-x64.zip" | Out-File SHA256.txt -Encoding ascii
      - uses: actions/upload-artifact@v4
        with:
          name: AIUsageTray-win-x64
          path: |
            AIUsageTray-win-x64.zip
            SHA256.txt
```

- [ ] **Step 2: README Windows section** (append before License in `README.md`):

```markdown
## Windows (system tray)

A .NET port lives in `windows/` — tray icon shows the highest utilization with traffic-light coloring; right-click for details.

- Requirements: Windows 10+, Claude Code installed and logged in with a subscription (OAuth) account **natively on Windows**. WSL installs and API-key/Bedrock/Vertex auth are not supported (the token file isn't where the app can see it).
- Token source: `%USERPROFILE%\.claude\.credentials.json` (or `%CLAUDE_CONFIG_DIR%`).
- Build from source (recommended): install the .NET 10 SDK, then
  `dotnet publish windows/AIUsage.Tray -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true`
- Prebuilt exe: see Releases (SHA-256 checksums attached). It is unsigned, so SmartScreen will warn — building from source avoids this.
```

- [ ] **Step 3: Commit, push, download CI artifact, cut pre-release**

```bash
cd ~/Projects/ai-usage-menubar
git add .github README.md README.ko.md && git commit -m "ci(windows): windows-latest publish workflow + README"
git push
# Pin the exact run — bare `gh run watch` can go interactive or track the wrong run
RUN_ID=$(gh run list -w windows-build -L1 --json databaseId -q '.[0].databaseId')
gh run watch "$RUN_ID" --exit-status
gh run download "$RUN_ID" -n AIUsageTray-win-x64 -D win-artifact
gh release create v1.1.0-rc1 --prerelease --title "v1.1.0-rc1 (Windows beta)" \
  --notes "Windows tray port for on-device verification. Unsigned; SHA256.txt covers the zip." \
  win-artifact/AIUsageTray-win-x64.zip win-artifact/SHA256.txt
```

Also update the README Windows section with the measured exe size (from the CI artifact) and a note that self-contained builds don't receive .NET runtime patches automatically (republished on .NET patch releases as needed).

- [ ] **Step 4: Hand user the verification checklist** (from DESIGN-windows.md §검수 체크리스트) with the pre-release URL. **User gate: 정식 v1.1.0 릴리스는 회사 PC 검수 통과 후.**

- [ ] **Step 5 (after user verification passes): final release**

```bash
cd ~/Projects/ai-usage-menubar
gh release create v1.1.0 --title "v1.1.0" \
  --notes "Adds Windows system-tray port. macOS app unchanged from v1.0.0." \
  win-artifact/AIUsageTray-win-x64.zip win-artifact/SHA256.txt "AI-Usage-v1.0.0.zip"
```

(macOS zip reused from the v1.0.0 asset — download it first with `gh release download v1.0.0 -p '*.zip'`. If the 검수 required code changes, rebuild via a fresh CI run and use that artifact instead.)
