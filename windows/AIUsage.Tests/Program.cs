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
        CredentialsTests.Run();
        StateTests.Run();
        DisplayTests.Run();
        ClientTests.Run().GetAwaiter().GetResult();
        CodexTests.Run().GetAwaiter().GetResult();
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
        var expectedReset = new DateTimeOffset(2026, 7, 23, 10, 0, 0, TimeSpan.Zero).AddTicks(1234560);
        TestHarness.Check(snap.Session.ResetsAt == expectedReset, "parse: fractional-seconds preserved exactly");

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

        (_, err) = UsageParser.Parse(Good.Replace("2026-07-23T10:00:00.123456+00:00", "23/07/2026 10:00"));
        TestHarness.Check(err != null, "parse: non-ISO date rejected");
    }
}

internal static class CredentialsTests
{
    public static void Run()
    {
        const long now = 1_800_000_000_000; // synthetic epoch ms
        var good = """{"claudeAiOauth":{"accessToken":"synthetic-token","expiresAt":1900000000000}}""";
        var (cred, err) = CredentialsLoader.ParseJson(good, now);
        TestHarness.Check(err == null && cred!.AccessToken == "synthetic-token", "cred: parse ok");
        TestHarness.Check(cred!.ExpiresAtMs == 1_900_000_000_000, "cred: expiresAt ms");

        (cred, err) = CredentialsLoader.ParseJson(
            """{"claudeAiOauth":{"accessToken":"synthetic-token","expiresAt":1700000000000}}""", now);
        TestHarness.Check(cred == null && err == "token expired", "cred: expired detected");

        (cred, err) = CredentialsLoader.ParseJson("{}", now);
        TestHarness.Check(cred == null && err != null, "cred: missing oauth block = error");

        (cred, err) = CredentialsLoader.ParseJson("nope", now);
        TestHarness.Check(cred == null && err == "credentials not valid JSON", "cred: invalid json = error");

        (cred, err) = CredentialsLoader.ParseJson(
            """{"claudeAiOauth":{"accessToken":"","expiresAt":1900000000000}}""", now);
        TestHarness.Check(cred == null && err == "credentials token empty", "cred: empty token rejected");

        (cred, err) = CredentialsLoader.ParseJson(
            """{"claudeAiOauth":{"accessToken":"t","expiresAt":"soon"}}""", now);
        TestHarness.Check(cred == null && err != null, "cred: non-numeric expiresAt = error");

        (_, err) = CredentialsLoader.LoadFromFile(
            Path.Combine(Path.GetTempPath(), "aiusage-definitely-missing.json"), now);
        TestHarness.Check(err == "credentials file not found", "cred: missing file reason (after 1 retry)");
    }
}

internal static class StateTests
{
    private static UsageSnapshot Snap(int s, int w, int m)
    {
        var t = DateTimeOffset.UtcNow.AddHours(3);
        return new UsageSnapshot(
            new(LimitKind.Session, s, t),
            new(LimitKind.WeeklyAll, w, t),
            new(LimitKind.WeeklyScoped, m, t));
    }

    public static void Run()
    {
        var ok = TrayStateMachine.Next(new TrayState.Loading(), Snap(64, 79, 80), null);
        TestHarness.Check(ok is TrayState.Ok, "state: loading→ok");

        var first = ((TrayState.Ok)ok).Snapshot;
        var deg = TrayStateMachine.Next(ok, null, "http 500");
        TestHarness.Check(deg is TrayState.Degraded d1 && d1.Last == first, "state: failure keeps exact last snapshot");

        var deg2 = TrayStateMachine.Next(deg, null, "timeout");
        TestHarness.Check(deg2 is TrayState.Degraded d2 && d2.Last == first, "state: repeated failure still keeps exact last");

        var early = TrayStateMachine.Next(new TrayState.Loading(), null, "no file");
        TestHarness.Check(early is TrayState.Degraded { Last: null }, "state: failure before first data = no last");

        var back = TrayStateMachine.Next(deg2, Snap(1, 2, 3), null);
        TestHarness.Check(back is TrayState.Ok, "state: recovery");
    }
}

internal static class DisplayTests
{
    public static void Run()
    {
        TestHarness.Check(Display.Severity(69) == Severity.Green, "disp: 69 green");
        TestHarness.Check(Display.Severity(70) == Severity.Orange, "disp: 70 orange");
        TestHarness.Check(Display.Severity(89) == Severity.Orange, "disp: 89 orange");
        TestHarness.Check(Display.Severity(90) == Severity.Red, "disp: 90 red");

        var hidden = new CodexState.Hidden();
        var t = DateTimeOffset.UtcNow.AddHours(3);
        var snap = new UsageSnapshot(
            new(LimitKind.Session, 64, t),
            new(LimitKind.WeeklyAll, 79, t),
            new(LimitKind.WeeklyScoped, 100, t));
        TestHarness.Check(Display.IconText(new TrayState.Ok(snap), hidden) == "100", "disp: 100 renders '100'");
        TestHarness.Check(Display.IconText(new TrayState.Degraded("x", null), hidden) == "!", "disp: degraded '!'");
        TestHarness.Check(Display.IconText(new TrayState.Loading(), hidden) == "…", "disp: loading ellipsis");

        // codex worst limit wins the icon number
        var codexHot = new CodexState.Ok([new CodexReading("5h", 40, null), new CodexReading("Weekly", 100, null)]);
        var snapLow = new UsageSnapshot(
            new(LimitKind.Session, 10, t), new(LimitKind.WeeklyAll, 20, t), new(LimitKind.WeeklyScoped, 30, t));
        TestHarness.Check(Display.IconText(new TrayState.Ok(snapLow), codexHot) == "100", "disp: codex max wins icon");
        TestHarness.Check(Display.CombinedMax(snapLow, hidden) == 30, "disp: hidden codex = claude max");

        TestHarness.Check(Display.Tooltip(new TrayState.Ok(snap), hidden) ==
            "Session 64% · Weekly 79% · Model 100%", "disp: ok tooltip");
        TestHarness.Check(Display.Tooltip(new TrayState.Ok(snapLow), codexHot) ==
            "Session 10% · Weekly 20% · Model 30% · Codex 40/100%", "disp: tooltip includes codex");
        var longReason = new string('x', 300);
        var tip = Display.Tooltip(new TrayState.Degraded(longReason, snap), hidden);
        var expectedFull = $"⚠ {longReason} · Session 64% · Weekly 79% · Model 100%";
        TestHarness.Check(tip == expectedFull[..127], "disp: tooltip is exact 127-char prefix");

        var t0 = new DateTimeOffset(2026, 7, 23, 10, 0, 0, TimeSpan.Zero);
        // expected date strings mirror ResetCountdown's local-time conversion (TZ-safe)
        static string D(DateTimeOffset x) { var l = x.ToLocalTime(); return $"{l.Month}/{l.Day}"; }
        var r1 = t0.AddHours(3).AddMinutes(12);
        TestHarness.Check(Display.ResetCountdown(r1, t0) == $"resets {D(r1)}, 3h 12m", "disp: countdown date h+m");
        TestHarness.Check(Display.ResetCountdown(t0.AddMinutes(59).AddSeconds(59), t0) == "resets in 59m", "disp: countdown 59m59s");
        var r2 = t0.AddHours(1);
        TestHarness.Check(Display.ResetCountdown(r2, t0) == $"resets {D(r2)}, 1h 0m", "disp: countdown exactly 1h");
        TestHarness.Check(Display.ResetCountdown(t0, t0) == "resets now", "disp: countdown at reset");
        TestHarness.Check(Display.ResetCountdown(t0.AddMinutes(-5), t0) == "resets now", "disp: countdown past reset");
        var r3 = t0.AddDays(5).AddHours(13);
        TestHarness.Check(Display.ResetCountdown(r3, t0) == $"resets {D(r3)}, 5d 13h", "disp: countdown days+hours");
    }
}

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

            // credentials reread every poll — account switch shows up on the next refresh
            // even though the old token is still valid (no 401 to trigger a reread)
            var tokensSeen = new List<string>();
            client = new AIUsage.Core.UsageClient(credPath, (token, ct) =>
            { tokensSeen.Add(token); return Task.FromResult((200, GoodBody)); });
            await client.FetchAsync(CancellationToken.None);
            await File.WriteAllTextAsync(credPath,
                """{"claudeAiOauth":{"accessToken":"rotated-token","expiresAt":9999999999999}}""");
            await client.FetchAsync(CancellationToken.None);
            TestHarness.Check(tokensSeen is ["synthetic-token", "rotated-token"], "client: account switch picked up next poll");

            // 401 rereads the file and passes the NEW token to the retry.
            // Rotation happens INSIDE the fake transport's first call — if it happened before,
            // the first read would already return the new token and the assertion would prove nothing.
            await File.WriteAllTextAsync(credPath,
                """{"claudeAiOauth":{"accessToken":"old-token","expiresAt":9999999999999}}""");
            tokensSeen.Clear();
            client = new AIUsage.Core.UsageClient(credPath, async (token, ct) =>
            {
                tokensSeen.Add(token);
                if (tokensSeen.Count == 1)
                {
                    await File.WriteAllTextAsync(credPath,
                        """{"claudeAiOauth":{"accessToken":"new-token","expiresAt":9999999999999}}""");
                    return (401, "");
                }
                return (200, GoodBody);
            });
            (snap, err) = await client.FetchAsync(CancellationToken.None);
            TestHarness.Check(err == null && tokensSeen is ["old-token", "new-token"], "client: 401 retry uses reread token");
        }
        finally { File.Delete(credPath); }
    }
}

internal static class CodexTests
{
    private const string GoodBody = """
    {"rate_limit":{
      "primary_window":{"used_percent":64.6,"limit_window_seconds":18000,"reset_at":1785000000},
      "secondary_window":{"used_percent":79.2,"limit_window_seconds":604800,"reset_at":1785400000}
    }}
    """;

    public static async Task Run()
    {
        // parser
        var (readings, err) = CodexParser.Parse(GoodBody);
        TestHarness.Check(err == null && readings!.Count == 2, "codex: parse good payload");
        TestHarness.Check(readings![0] is { Name: "5h", Percent: 65 }, "codex: primary window name+rounding");
        TestHarness.Check(readings[1] is { Name: "Weekly", Percent: 79 }, "codex: weekly window");
        TestHarness.Check(readings[0].ResetsAt == DateTimeOffset.FromUnixTimeSeconds(1_785_000_000), "codex: reset epoch seconds");

        (_, err) = CodexParser.Parse("""{"rate_limit":{}}""");
        TestHarness.Check(err != null, "codex: no windows = error");
        (_, err) = CodexParser.Parse("""{"rate_limit":{"primary_window":{"used_percent":-1}}}""");
        TestHarness.Check(err != null, "codex: negative percent = error");
        (readings, err) = CodexParser.Parse("""{"rate_limit":{"primary_window":{"used_percent":150}}}""");
        TestHarness.Check(err == null && readings![0] is { Name: "Limit", Percent: 100, ResetsAt: null }, "codex: clamp + missing fields tolerated");
        (_, err) = CodexParser.Parse("not json");
        TestHarness.Check(err != null, "codex: invalid json = error");
        TestHarness.Check(CodexParser.WindowName(1800) == "30m", "codex: minutes window name");

        // .5 must round AwayFromZero (64.5 → 65), not banker's ToEven (→ 64)
        (readings, err) = CodexParser.Parse("""{"rate_limit":{"primary_window":{"used_percent":64.5}}}""");
        TestHarness.Check(err == null && readings![0].Percent == 65, "codex: .5 rounds away from zero");
        // finite but absurd epoch must not throw — dropped as no reset info
        (readings, err) = CodexParser.Parse("""{"rate_limit":{"primary_window":{"used_percent":1,"reset_at":1e30}}}""");
        TestHarness.Check(err == null && readings![0].ResetsAt == null, "codex: out-of-range epoch tolerated");

        // auth
        var (auth, aerr) = CodexAuthLoader.ParseJson(
            """{"tokens":{"access_token":"synthetic","account_id":"acc-1"}}""");
        TestHarness.Check(aerr == null && auth!.AccessToken == "synthetic" && auth.AccountId == "acc-1", "codex: auth parse ok");
        TestHarness.Check(auth!.ToString() == "CodexAuth(<redacted>)", "codex: auth ToString redacted");
        (auth, aerr) = CodexAuthLoader.ParseJson("""{"tokens":{"access_token":""}}""");
        TestHarness.Check(auth == null && aerr != null, "codex: empty token rejected");

        var (_, _, loggedIn) = CodexAuthLoader.LoadFromFile(
            Path.Combine(Path.GetTempPath(), "aiusage-codex-missing.json"));
        TestHarness.Check(!loggedIn, "codex: missing auth file = not logged in");

        // state machine
        var st = CodexStateMachine.Next(new CodexState.Loading(), new CodexFetchResult(null, null, false));
        TestHarness.Check(st is CodexState.Hidden, "codex: logged-out = hidden");
        var okReadings = new List<CodexReading> { new("Weekly", 12, null) };
        st = CodexStateMachine.Next(st, new CodexFetchResult(okReadings, null, true));
        TestHarness.Check(st is CodexState.Ok, "codex: readings = ok");
        st = CodexStateMachine.Next(st, new CodexFetchResult(null, "boom", true));
        TestHarness.Check(st is CodexState.Degraded { Reason: "boom" } d && d.Last == okReadings, "codex: failure keeps last readings");

        // client
        var authPath = Path.Combine(Path.GetTempPath(), $"aiusage-codex-test-{Environment.ProcessId}.json");
        await File.WriteAllTextAsync(authPath,
            """{"tokens":{"access_token":"old-token","account_id":"acc-1"}}""");
        try
        {
            var seen = new List<string>();
            var client = new CodexClient(authPath, async (a, ct) =>
            {
                seen.Add(a.AccessToken);
                if (seen.Count == 1)
                {
                    await File.WriteAllTextAsync(authPath,
                        """{"tokens":{"access_token":"new-token"}}""");
                    return (401, "");
                }
                return (200, GoodBody);
            });
            var res = await client.FetchAsync(CancellationToken.None);
            TestHarness.Check(res.Error == null && res.Readings != null && seen is ["old-token", "new-token"],
                "codex client: 401 retry uses reread token");

            client = new CodexClient(authPath, (a, ct) => Task.FromResult((500, "boom")));
            res = await client.FetchAsync(CancellationToken.None);
            TestHarness.Check(res is { Readings: null, Error: "codex API HTTP 500", LoggedIn: true }, "codex client: 5xx no body echo");

            client = new CodexClient(authPath + ".missing", (a, ct) => Task.FromResult((200, GoodBody)));
            res = await client.FetchAsync(CancellationToken.None);
            TestHarness.Check(res is { LoggedIn: false, Error: null }, "codex client: missing file = hidden, no error");

            // persistent 401 → exactly one retry, then degraded
            var calls = 0;
            client = new CodexClient(authPath, (a, ct) => { calls++; return Task.FromResult((401, "")); });
            res = await client.FetchAsync(CancellationToken.None);
            TestHarness.Check(res.Readings == null && res.Error != null && calls == 2, "codex client: persistent 401 = one retry then degraded");

            // auth reread between two SUCCESSFUL polls (account switch, no 401)
            seen.Clear();
            client = new CodexClient(authPath, (a, ct) => { seen.Add(a.AccessToken); return Task.FromResult((200, GoodBody)); });
            await client.FetchAsync(CancellationToken.None);
            await File.WriteAllTextAsync(authPath, """{"tokens":{"access_token":"switched-token"}}""");
            await client.FetchAsync(CancellationToken.None);
            TestHarness.Check(seen is [_, "switched-token"], "codex client: account switch picked up next poll");

            // transport exception → degraded reason, no throw
            client = new CodexClient(authPath, (a, ct) => throw new HttpRequestException());
            res = await client.FetchAsync(CancellationToken.None);
            TestHarness.Check(res is { Readings: null, Error: "codex network error" }, "codex client: network error contained");

            // degraded codex surfaces in tooltip
            var t = DateTimeOffset.UtcNow.AddHours(3);
            var snap = new UsageSnapshot(
                new(LimitKind.Session, 1, t), new(LimitKind.WeeklyAll, 2, t), new(LimitKind.WeeklyScoped, 3, t));
            var degraded = new CodexState.Degraded("boom", [new CodexReading("Weekly", 12, null)]);
            TestHarness.Check(Display.Tooltip(new TrayState.Ok(snap), degraded).EndsWith("· ⚠ Codex"),
                "codex: degraded visible in tooltip");
        }
        finally { File.Delete(authPath); }
    }
}
