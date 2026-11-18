using MtgaDash.Ingestion;
using MtgaDash.Parsing;

namespace MtgaDash.Tests;

[TestClass]
public sealed class ActivityLogTests
{
    private static readonly string[] AfterMatch =
    [
        "<== EventClaimPrize(11111111-0000-4000-8000-000000000005)",
        """{"Changes":[{"Source":"EventPrize","GoldDelta":250,"GemsDelta":0}]}""",
    ];

    /// <summary>Mentions wildcards but carries no inventory the parser reads.</summary>
    private static readonly string[] Unread =
    [
        "<== EventGetCoursesV2(22222222-0000-4000-8000-000000000006)",
        """{"Courses":[{"CourseId":"x","WildCardRedemptions":1}]}""",
    ];

    private static List<LogEvent> Parse(IEnumerable<string> lines) => new LogParser(logTimeZone: Fixtures.Utc).ParseAll(lines).ToList();

    [TestMethod]
    public void Every_message_is_reported_with_whether_it_was_read_and_what_it_mentions()
    {
        var seen = Parse([.. Fixtures.AllerclearLog(), .. AfterMatch, .. Unread]).OfType<MessageSeen>().ToList();

        var startHook = seen.Single(m => m.Method == "StartHook");
        Assert.IsTrue(startHook.Handled);
        CollectionAssert.Contains(startHook.Hints.ToList(), "InventoryInfo");

        var prize = seen.Single(m => m.Method == "EventClaimPrize");
        Assert.IsTrue(prize.Handled, "per-match gold changes are read");
        CollectionAssert.AreEquivalent(new[] { "GoldDelta", "GemsDelta" }, prize.Hints.ToList());

        var unread = seen.Single(m => m.Method == "EventGetCoursesV2");
        Assert.IsFalse(unread.Handled);
        CollectionAssert.AreEqual(new[] { "WildCard" }, unread.Hints.ToList());
        Assert.AreEqual("Allerclear#92603", unread.DisplayName);

        var upsert = seen.Single(m => m.Method == "DeckUpsertDeckV3");
        Assert.AreEqual(("request", true), (upsert.Direction, upsert.Handled));
    }

    [TestMethod]
    public void A_message_that_mentions_inventory_but_is_not_read_gets_its_own_row()
    {
        var log = new ActivityLog();
        foreach (var e in Parse([.. Fixtures.AllerclearLog(), .. AfterMatch, .. Unread])) log.Record(Path.Combine("logs", "Player.log"), e);

        var entries = log.Entries();
        var notRead = entries.First();
        Assert.AreEqual("not read", notRead.Kind, "newest first");
        StringAssert.Contains(notRead.Summary, "EventGetCoursesV2");
        Assert.AreEqual("Player.log", notRead.File);

        var kinds = entries.Select(e => e.Kind).ToList();
        CollectionAssert.IsSubsetOf(new[] { "client start", "login", "inventory", "decks", "rank", "deck saved" }, kinds);
        StringAssert.Contains(entries.Single(e => e.Kind == "inventory").Summary, "gems 450, gold 2,150, wildcards 39/25/8/7, 2 boosters");

        StringAssert.Contains(entries.Single(e => e.Kind == "inventory change").Summary, "+250 gold (EventClaimPrize)");
        var prize = log.Messages().Single(m => m.Method == "EventClaimPrize");
        Assert.AreEqual((1L, 1L), (prize.Count, prize.Handled));
    }

    [TestMethod]
    public void A_partial_inventory_update_says_so()
    {
        var i = new InventorySnapshot("a#1", DateTime.UtcNow, 300, 900, null, null, null, null, null, 4, null, null);
        Assert.AreEqual("gems 300, gold 900 (partial update)", ActivityLog.Describe(i));
    }

    [TestMethod]
    public void Tail_returns_the_last_lines_cut_to_length()
    {
        using var t = new TempDb();
        var path = t.File("tail.log");
        File.WriteAllText(path, string.Join("\n", Enumerable.Range(1, 100).Select(i => $"line {i}")) + "\n" + new string('x', 1000) + "\n");
        var tail = ActivityLog.Tail(path, 3, maxChars: 50);
        Assert.AreEqual(3, tail.Count);
        Assert.AreEqual("line 99", tail[0]);
        StringAssert.EndsWith(tail[2], "(1,000 characters)");
        Assert.AreEqual(101, ActivityLog.Tail(path, 500).Count);
    }
}

[TestClass]
public sealed class ActivityHistoryTests
{
    [TestMethod]
    public void Entries_survive_a_restart_once_and_expire_after_a_day()
    {
        using var t = new TempDb();
        var events = new LogParser(logTimeZone: Fixtures.Utc).ParseAll(Fixtures.AllerclearLog()).ToList();
        var first = new ActivityLog(t.Db);
        foreach (var e in events) first.Record("Player.log", e);
        first.Flush();
        var count = first.Entries().Count;
        Assert.IsTrue(count >= 6);

        var restarted = new ActivityLog(t.Db);
        CollectionAssert.AreEqual(first.Entries().Select(e => e.Summary).ToList(), restarted.Entries().Select(e => e.Summary).ToList());

        // The same lines again (archived copy, re-parse) are stored once.
        foreach (var e in events) restarted.Record("Player.log", e);
        restarted.Flush();
        using (var c = t.Db.Open())
        {
            Assert.AreEqual(count, Dapper.SqlMapper.ExecuteScalar<int>(c, "SELECT COUNT(*) FROM ActivityEntries"));
            Dapper.SqlMapper.Execute(c, "UPDATE ActivityEntries SET AtUtc = '2000-01-01T00:00:00.000Z'");
        }
        restarted.Flush();
        Assert.AreEqual(0, new ActivityLog(t.Db).Entries().Count, "older than 24 hours is deleted");
    }
}
