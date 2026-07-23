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
