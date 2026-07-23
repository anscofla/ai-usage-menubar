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
