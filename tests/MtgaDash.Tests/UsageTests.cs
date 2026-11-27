using Dapper;
using MtgaDash.Usage;

namespace MtgaDash.Tests;

[TestClass]
public sealed class UsageTests
{
    [TestMethod]
    [DataRow("Copy base1+corsair@example.com", "Copy <email>")]
    [DataRow("Logged in as Corsair#10001 today", "Logged in as <player> today")]
    [DataRow(@"Could not read C:\Users\owner\mtga\Player.log now", "Could not read <path> now")]
    [DataRow("a|b\nc", "a/b c")]
    public void Free_text_loses_emails_names_and_paths(string text, string expected) => Assert.AreEqual(expected, Redact.Text(text));

    [TestMethod]
    public void Only_known_kinds_are_stored_and_values_are_redacted()
    {
        using var t = new TempDb();
        var log = new UsageLog(t.Db, new MtgaDashOptions());
        var now = new DateTime(2026, 11, 27, 12, 0, 0, DateTimeKind.Utc);
        var stored = log.Add([
            new("2026-11-27T11:59:00Z", "s1", "view", "accounts", null),
            new("2026-11-27T11:59:30Z", "s1", "click", "Copy base1+corsair@example.com", "accounts"),
            new("2026-11-27T11:59:40Z", "s1", "keystrokes", "secret", null),
            new("2026-11-27T11:59:50Z", "s1", "view", "", null),
            new("1999-01-01T00:00:00Z", "s1", "view", "cards", null),
        ], now);
        Assert.AreEqual(3, stored, "an unknown kind and an empty name are dropped");
        using var c = t.Db.Open();
        var rows = c.Query<(string AtUtc, string Name)>("SELECT AtUtc, Name FROM UsageEvents ORDER BY Id").ToList();
        Assert.AreEqual("Copy <email>", rows[1].Name);
        Assert.AreEqual("2026-11-27T12:00:00.000Z", rows[2].AtUtc, "a time far from now is replaced by now");
    }

    [TestMethod]
    public void Old_events_are_pruned_and_tracking_can_be_turned_off()
    {
        using var t = new TempDb();
        var now = new DateTime(2026, 11, 27, 12, 0, 0, DateTimeKind.Utc);
        using (var c = t.Db.Open())
            c.Execute("INSERT INTO UsageEvents(AtUtc, Session, Kind, Name) VALUES ('2026-01-01T00:00:00.000Z', 'old', 'view', 'accounts')");
        new UsageLog(t.Db, new MtgaDashOptions { UsageRetentionDays = 30 }).Add([new(null, "s", "view", "cards", null)], now);
        Assert.AreEqual(0, new UsageLog(t.Db, new MtgaDashOptions { TrackUsage = false }).Add([new(null, "s", "view", "cards", null)], now));
        using var c2 = t.Db.Open();
        CollectionAssert.AreEqual(new[] { "s" }, c2.Query<string>("SELECT Session FROM UsageEvents").ToArray());
    }

    [TestMethod]
    public void Active_time_drops_idle_gaps()
    {
        var t0 = new DateTime(2026, 11, 27, 12, 0, 0, DateTimeKind.Utc);
        Assert.AreEqual(TimeSpan.FromMinutes(3), UsageReport.Active([t0, t0.AddMinutes(1), t0.AddMinutes(3), t0.AddHours(2)]));
    }

    [TestMethod]
    public void Request_timings_are_kept_per_endpoint()
    {
        var r = new RequestTimings();
        r.Record("GET /api/accounts", 10);
        r.Record("GET /api/accounts", 700);
        var row = r.Rows().Single();
        Assert.AreEqual((2L, 355.0, 700.0, 1L), (row.Count, row.MeanMs, row.MaxMs, row.Over500Ms));
    }
}
