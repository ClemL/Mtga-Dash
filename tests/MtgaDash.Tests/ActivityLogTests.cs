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

    private static List<LogEvent> Parse(IEnumerable<string> lines) => new LogParser(logTimeZone: Fixtures.Utc).ParseAll(lines).ToList();

    [TestMethod]
    public void Every_message_is_reported_with_whether_it_was_read_and_what_it_mentions()
    {
        var seen = Parse([.. Fixtures.AllerclearLog(), .. AfterMatch]).OfType<MessageSeen>().ToList();

        var startHook = seen.Single(m => m.Method == "StartHook");
        Assert.IsTrue(startHook.Handled);
        CollectionAssert.Contains(startHook.Hints.ToList(), "InventoryInfo");

        var prize = seen.Single(m => m.Method == "EventClaimPrize");
        Assert.IsFalse(prize.Handled, "per-match changes are not read yet");
        CollectionAssert.AreEquivalent(new[] { "GoldDelta", "GemsDelta" }, prize.Hints.ToList());
        Assert.AreEqual("Allerclear#92603", prize.DisplayName);

        var upsert = seen.Single(m => m.Method == "DeckUpsertDeckV3");
        Assert.AreEqual(("request", true), (upsert.Direction, upsert.Handled));
    }

    [TestMethod]
    public void A_message_that_mentions_inventory_but_is_not_read_gets_its_own_row()
    {
        var log = new ActivityLog();
        foreach (var e in Parse([.. Fixtures.AllerclearLog(), .. AfterMatch])) log.Record(Path.Combine("logs", "Player.log"), e);

        var entries = log.Entries();
        var notRead = entries.First();
        Assert.AreEqual("not read", notRead.Kind, "newest first");
        StringAssert.Contains(notRead.Summary, "EventClaimPrize");
        Assert.AreEqual("Player.log", notRead.File);

        var kinds = entries.Select(e => e.Kind).ToList();
        CollectionAssert.IsSubsetOf(new[] { "client start", "login", "inventory", "decks", "rank", "deck saved" }, kinds);
        StringAssert.Contains(entries.Single(e => e.Kind == "inventory").Summary, "gems 450, gold 2,150, wildcards 39/25/8/7, 2 boosters");

        var prize = log.Messages().Single(m => m.Method == "EventClaimPrize");
        Assert.AreEqual((1L, 0L), (prize.Count, prize.Handled));
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
