using System.Text;
using Dapper;

namespace MtgaDash.Tests;

[TestClass]
public sealed class IngestionTests
{
    /// <summary>Every row the ingestor writes, in a stable order, so two databases can be compared.</summary>
    private static string Dump(TempDb t)
    {
        using var c = t.Db.Open();
        var sb = new StringBuilder();
        foreach (var r in c.Query("SELECT DisplayName, NamePart, FirstSeenUtc, LastLoginUtc FROM Players ORDER BY 1")) sb.AppendLine(string.Join("|", (IDictionary<string, object>)r));
        foreach (var r in c.Query("SELECT DisplayName, CapturedAtUtc, Gems, Gold, WcCommon, WcUncommon, WcRare, WcMythic, VaultProgress, WcTrackPosition, CustomTokensJson FROM Snapshots ORDER BY 1, 2")) sb.AppendLine(string.Join("|", (IDictionary<string, object>)r));
        foreach (var r in c.Query("SELECT s.DisplayName, b.SetCode, b.CollationId, b.Count FROM Boosters b JOIN Snapshots s ON s.Id = b.SnapshotId ORDER BY 1, 2")) sb.AppendLine(string.Join("|", (IDictionary<string, object>)r));
        foreach (var r in c.Query("SELECT DisplayName, DeckId, Name, Format, IsPrecon, LastPlayed, LastUpdated, CardsJson FROM Decks ORDER BY 1, 2")) sb.AppendLine(string.Join("|", (IDictionary<string, object>)r));
        foreach (var r in c.Query("SELECT * FROM DeckLists ORDER BY 1, 2, 3")) sb.AppendLine(string.Join("|", (IDictionary<string, object>)r));
        foreach (var r in c.Query("SELECT * FROM Ranks ORDER BY 1, 2")) sb.AppendLine(string.Join("|", (IDictionary<string, object>)r));
        return sb.ToString();
    }

    private static string FullLog() => Fixtures.Text([
        .. Fixtures.AllerclearLog(),
        .. Fixtures.EventJoin(500, 2300),
        .. Fixtures.TwoLoginsDetailedOff(),
    ]);

    [TestMethod]
    public void Ingest_writes_players_snapshot_decks_and_rank()
    {
        using var t = new TempDb();
        File.WriteAllText(t.File("Player_20261113_004330.log"), FullLog());
        var r = t.Ingestor.IngestFile(t.File("Player_20261113_004330.log"));
        Assert.AreEqual(3, r.Counts.Logins);
        Assert.AreEqual(3, r.Counts.NewPlayers);

        var counts = t.Queries.Counts();
        Assert.AreEqual(3, counts.Players);
        Assert.AreEqual(1, counts.Snapshots, "the EventJoin inventory merges into the session's StartHook snapshot");
        Assert.AreEqual(120, counts.Decks);
        Assert.AreEqual(4, counts.CustomDecks);
        Assert.AreEqual(1, counts.DeckLists);
        Assert.AreEqual(1, counts.Ranks);

        using var c = t.Db.Open();
        var snap = c.QuerySingle<(long Gems, long Gold, long WcRare, long Track)>("SELECT Gems, Gold, WcRare, WcTrackPosition FROM Snapshots");
        Assert.AreEqual((500L, 2300L, 8L, 4L), snap, "EventJoin updates gems, gold and track; wildcards keep StartHook's values");
        Assert.AreEqual(2, c.ExecuteScalar<int>("SELECT COUNT(*) FROM Boosters"));
    }

    [TestMethod]
    public void Ingesting_the_same_file_twice_adds_nothing()
    {
        using var t = new TempDb();
        var path = t.File("a.log");
        File.WriteAllText(path, FullLog());
        t.Ingestor.IngestFile(path);
        var before = Dump(t);
        var again = t.Ingestor.IngestFile(path);
        Assert.AreEqual(0, again.BytesRead);

        // A second copy of the same content under another name (the archiver and the live log) changes nothing either.
        File.WriteAllText(t.File("b.log"), FullLog());
        t.Ingestor.IngestFile(t.File("b.log"));
        Assert.AreEqual(before, Dump(t));
    }

    [TestMethod]
    [DataRow(0.3)]
    [DataRow(0.5)]
    [DataRow(0.77)]
    public void Two_chunks_split_mid_line_equal_one_pass(double fraction)
    {
        var text = FullLog();
        var bytes = Encoding.UTF8.GetBytes(text);
        var cut = (int)(bytes.Length * fraction);
        while (bytes[cut] == (byte)'\n') cut++; // make sure the cut lands inside a line

        using var once = new TempDb();
        File.WriteAllBytes(once.File("p.log"), bytes);
        once.Ingestor.IngestFile(once.File("p.log"));

        using var twice = new TempDb();
        var path = twice.File("p.log");
        File.WriteAllBytes(path, bytes[..cut]);
        var first = twice.Ingestor.IngestFile(path);
        Assert.IsTrue(first.BytesRead < cut, "the partial last line is left for the next poll");
        using (var fs = new FileStream(path, FileMode.Append)) fs.Write(bytes, cut, bytes.Length - cut);
        twice.Ingestor.IngestFile(path);

        Assert.AreEqual(Dump(once), Dump(twice));
    }

    [TestMethod]
    public void A_shrunk_file_is_re_parsed_from_the_start_without_duplicates()
    {
        using var t = new TempDb();
        var path = t.File("Player.log");
        File.WriteAllText(path, FullLog());
        t.Ingestor.IngestFile(path);
        var full = Dump(t);

        // MTGA rotated the log: the new file is shorter and starts with the same session.
        File.WriteAllText(path, Fixtures.Text(Fixtures.AllerclearLog()));
        var r = t.Ingestor.IngestFile(path);
        Assert.IsTrue(r.Restarted);
        Assert.AreEqual(full, Dump(t));
    }

    [TestMethod]
    public void A_changed_first_4KB_is_re_parsed_from_the_start()
    {
        using var t = new TempDb();
        var path = t.File("Player.log");
        File.WriteAllText(path, FullLog());
        t.Ingestor.IngestFile(path);

        // Same length or longer, different start: a new session for another account.
        var other = Fixtures.Text([Fixtures.Startup("11/14/2026 8:00:00 AM"), Fixtures.Login("other#1"), .. Fixtures.StartHook("2026-11-14T13:00:00Z", gems: 1)]);
        File.WriteAllText(path, other + FullLog());
        var r = t.Ingestor.IngestFile(path);
        Assert.IsTrue(r.Restarted);
        var counts = t.Queries.Counts();
        Assert.AreEqual(4, counts.Players);
        Assert.AreEqual(2, counts.Snapshots);
        Assert.AreEqual(240, counts.Decks);
    }

    [TestMethod]
    public void A_lagging_archive_copy_ingested_after_the_live_log_does_not_roll_inventory_back()
    {
        using var t = new TempDb();
        File.WriteAllText(t.File("Player.log"), FullLog());
        t.Ingestor.IngestFile(t.File("Player.log"));
        var live = Dump(t);
        // The archiver's copy stopped before the EventJoin that changed gems and gold.
        File.WriteAllText(t.File("Player_20261113_004330.log"), Fixtures.Text(Fixtures.AllerclearLog()));
        t.Ingestor.IngestFile(t.File("Player_20261113_004330.log"));
        Assert.AreEqual(live, Dump(t));
    }

    [TestMethod]
    public void A_file_smaller_than_4KB_that_grows_is_not_mistaken_for_a_rotation()
    {
        using var t = new TempDb();
        var path = t.File("small.log");
        File.WriteAllText(path, Fixtures.Text([Fixtures.Startup(), Fixtures.Login("a#1")]));
        t.Ingestor.IngestFile(path);
        File.AppendAllText(path, Fixtures.Text(Fixtures.Rank()));
        var r = t.Ingestor.IngestFile(path);
        Assert.IsFalse(r.Restarted);
        Assert.AreEqual(1, r.Counts.Ranks);
    }

    [TestMethod]
    public void An_older_archive_ingested_later_does_not_roll_a_deck_back()
    {
        using var t = new TempDb();
        File.WriteAllText(t.File("new.log"), Fixtures.Text([Fixtures.Startup(), Fixtures.Login("a#1"), .. Fixtures.StartHook("2026-11-13T05:00:00Z"), Fixtures.DeckUpsert()]));
        t.Ingestor.IngestFile(t.File("new.log"));
        var body = System.Text.Json.JsonSerializer.Serialize(new { Summaries = new[] { Fixtures.Deck("c0", "Old name", "Historic", null) } });
        File.WriteAllText(t.File("old.log"), Fixtures.Text([Fixtures.Startup("10/01/2026 1:00:00 PM"), Fixtures.Login("a#1"), "<== DeckGetDeckSummariesV3(q)", body]));
        t.Ingestor.IngestFile(t.File("old.log"));

        using var c = t.Db.Open();
        Assert.AreEqual("Mono red", c.ExecuteScalar<string>("SELECT Name FROM Decks WHERE DeckId = 'c0'"));
        Assert.AreEqual("2026-10-01T13:00:00.000Z", c.ExecuteScalar<string>("SELECT FirstSeenUtc FROM Players WHERE DisplayName = 'a#1'"));
    }

    [TestMethod]
    public void The_file_stays_readable_while_another_process_holds_it_open_for_writing()
    {
        using var t = new TempDb();
        var path = t.File("Player.log");
        using var writer = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        var bytes = Encoding.UTF8.GetBytes(FullLog());
        writer.Write(bytes);
        writer.Flush();
        var r = t.Ingestor.IngestFile(path);
        Assert.AreEqual(3, r.Counts.Logins);
    }
}
