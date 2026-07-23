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

        var t = DateTimeOffset.UtcNow.AddHours(3);
        var snap = new UsageSnapshot(
            new(LimitKind.Session, 64, t),
            new(LimitKind.WeeklyAll, 79, t),
            new(LimitKind.WeeklyScoped, 100, t));
        TestHarness.Check(Display.IconText(new TrayState.Ok(snap)) == "100", "disp: 100 renders '100'");
        TestHarness.Check(Display.IconText(new TrayState.Degraded("x", null)) == "!", "disp: degraded '!'");
        TestHarness.Check(Display.IconText(new TrayState.Loading()) == "…", "disp: loading ellipsis");

        TestHarness.Check(Display.Tooltip(new TrayState.Ok(snap)) ==
            "Session 64% · Weekly 79% · Model 100%", "disp: ok tooltip");
        var longReason = new string('x', 300);
        var tip = Display.Tooltip(new TrayState.Degraded(longReason, snap));
        var expectedFull = $"⚠ {longReason} · Session 64% · Weekly 79% · Model 100%";
        TestHarness.Check(tip == expectedFull[..127], "disp: tooltip is exact 127-char prefix");

        var t0 = new DateTimeOffset(2026, 7, 23, 10, 0, 0, TimeSpan.Zero);
        TestHarness.Check(Display.ResetCountdown(t0.AddHours(3).AddMinutes(12), t0) == "resets in 3h 12m", "disp: countdown h+m");
        TestHarness.Check(Display.ResetCountdown(t0.AddMinutes(59).AddSeconds(59), t0) == "resets in 59m", "disp: countdown 59m59s");
        TestHarness.Check(Display.ResetCountdown(t0.AddHours(1), t0) == "resets in 1h 0m", "disp: countdown exactly 1h");
        TestHarness.Check(Display.ResetCountdown(t0, t0) == "resets now", "disp: countdown at reset");
        TestHarness.Check(Display.ResetCountdown(t0.AddMinutes(-5), t0) == "resets now", "disp: countdown past reset");
        TestHarness.Check(Display.ResetCountdown(t0.AddDays(2), t0) == "resets in 48h 0m", "disp: countdown multi-day hours");
    }
}
